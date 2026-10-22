using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private CloudSave SelectedLocalSave() => SaveList.SelectedItem is SavedDocument doc && FindLinkedSave(doc) is { IsDemo: false } save
        ? save : throw new InvalidOperationException("请先选择一份真实角色存档。");

    private IReadOnlySet<string> LiveLocalSaveIds() => _simulation!.State.Saves
        // Ignored records still own their bindings; hiding them must not make their cloud slots orphaned.
        .Where(save => save.OwnerId == CloudSimulation.PreviewOwnerId && !save.IsDemo)
        .Select(save => save.Id).ToHashSet(StringComparer.Ordinal);

    private async Task SetRemoteSyncAsync(bool enabled, bool reuseLocalBinding = true)
    {
        var save = SelectedLocalSave();
        try
        {
        if (_desktopSync is null) await ReadDesktopCloudAsync();
        // The local off switch always works, even if the server's last write still needs a receipt.
        if (!enabled) _desktopSync!.SetEnabled(save.Id, false);
        if (enabled && !await ConfirmRemoteSharingAsync()) return;
        await RecoverDesktopOperationsAsync(save.Id);
        await ReadDesktopCloudAsync();
        if (enabled && reuseLocalBinding && await ChooseExistingLocalForEnableAsync(CurrentLocalSave(save.Id))) return;
        if (enabled && await ChooseExistingCloudForEnableAsync(CurrentLocalSave(save.Id))) return;
        _desktopSync!.RetireKnownFailedOperations(save);
        if (enabled) _desktopSync.PrepareCloudReenable(CurrentLocalSave(save.Id), _remoteSaves, LiveLocalSaveIds());
        _desktopSync.SetEnabled(save.Id, enabled);
        if (enabled)
        {
            if (RemoteSave(save.Id) is { } stopped && stopped["syncEnabled"]?.GetValue<bool>() == false)
            {
                var resume = _desktopSync.PrepareSyncState(save.Id, stopped, true, acceptRemoteChanges: true);
                await _desktopSync.ExecuteAsync(resume, SendDesktopAsync, _desktopLifetime.Token);
                await ReadDesktopCloudAsync();
            }
            await SyncDesktopSavesAsync(true, save.Id);
        }
        else if (RemoteSave(save.Id) is not null) await StopRemoteSyncAsync(save, true);
        StatusText.Text = enabled ? "云同步已开启，本地与云端更新会自动同步，历史版本保留在本机。" : "已停止同步，最近成功的云端存档仍然保留。";
        }
        catch (Exception error) when (IsDesktopError(error))
        { _desktopSync?.RecordFailure(save.Id, error.Message); throw; }
    }

    private async Task SyncDesktopSavesAsync(bool manual, string? selectedLocalId = null)
    {
        if (_simulation is null || _desktopSession?.IsAllowed != true) return;
        if (_desktopSync is null) await ReadDesktopCloudAsync();
        await ReadDesktopCloudAsync();
        await FinishIgnoredCloudStopsAsync();
        if (manual)
        {
            if (!await ConfirmRemoteSharingAsync()) return;
        }
        else if (!_desktopConsentConfirmed)
        {
            var notice = await SendDesktopAsync("GET", "/api/desktop/cloud-consent", null, _desktopLifetime.Token);
            _desktopConsentConfirmed = notice["confirmed"]?.GetValue<bool>() == true;
            if (!_desktopConsentConfirmed) return;
        }
        await RecoverDesktopOperationsAsync(selectedLocalId);
        var updated = 0;
        foreach (var save in _simulation.State.Saves.Where(s => s.OwnerId == CloudSimulation.PreviewOwnerId && !s.IsDemo
                     && !s.IsIgnored
                     && (selectedLocalId is null || s.Id == selectedLocalId)).ToList())
        {
            if (_closing || _desktopSession?.IsAllowed != true) break;
            var binding = _desktopSync!.GetBinding(save.Id);
            try
            {
                if (manual) _desktopSync.RetireKnownFailedOperations(save);
                if (!binding.Enabled)
                {
                    if (binding.AcceptedSyncEnabled && (manual || binding.LastError.Length == 0) && RemoteSave(save.Id) is not null)
                        await StopRemoteSyncAsync(save, manual);
                    continue;
                }
                if (binding.CloudRevision == 0 && RemoteSave(save.Id) is null)
                {
                    var sameIdentity = _desktopSync.FindUniqueCloudSaveByIdentity(save, _remoteSaves);
                    if (sameIdentity is not null)
                    {
                        await LinkLocalToCloudAsync(save.Id, sameIdentity["saveId"]!.ToString(), true);
                        updated++;
                        continue;
                    }
                    if (save.IsMain && RemoteCharacter(save) is { } withoutMain && withoutMain["mainSaveId"] is null)
                    {
                        var firstMain = _desktopSync.PrepareMainSwitch(save, withoutMain, null, acceptRemoteChanges: manual, allCloudSaves: _remoteSaves);
                        var committed = await ExecuteDesktopWriteAsync(save, firstMain, manual, () =>
                        {
                            var latest = RemoteCharacter(save) ?? throw new InvalidOperationException("云端角色已变化，请刷新后重试。");
                            if (latest["mainSaveId"] is not null)
                                throw new DesktopCloudConflictException("另一台电脑已经设置了云主档，请刷新并确认主档关系后重试。");
                            return _desktopSync.PrepareMainSwitch(save, latest, RemoteSave(save.Id), acceptRemoteChanges: manual, allCloudSaves: _remoteSaves);
                        });
                        if (committed is not null) ApplyCommittedRemoteMain(committed);
                        await ReadDesktopCloudAsync();
                    }
                    else await UploadDesktopSaveAsync(save, manual);
                    updated++;
                    continue;
                }
                var decision = _desktopSync.GetReconciliationDecision(save, RemoteSave(save.Id), RemoteCharacter(save));
                if (decision.Action == DesktopCloudReconciliationAction.Stopped)
                {
                    _desktopSync.SetEnabled(save.Id, false);
                    binding.AcceptedSyncEnabled = false;
                    binding.Status = "disabled"; binding.LastError = ""; _desktopSync.Save();
                    StatusText.Text = "云端已停止同步，本地存档与历史仍然保留。";
                    continue;
                }
                if (decision.Action == DesktopCloudReconciliationAction.Deleted)
                {
                    _desktopSync.MarkRemoteDeleted(save.Id);
                    StatusText.Text = "对应云档已删除，已停止自动同步，本地存档与历史仍然保留。";
                    continue;
                }
                if (!manual && binding.Status == "conflict" && binding.LastError.Length > 0) continue;
                switch (decision.Action)
                {
                    case DesktopCloudReconciliationAction.None:
                        _desktopSync.AcceptObservedMetadata(save, RemoteSave(save.Id)!, RemoteCharacter(save)!);
                        if (manual) { await UploadDesktopSaveAsync(save, true); updated++; }
                        break;
                    case DesktopCloudReconciliationAction.Upload:
                        if (RemoteSave(save.Id) is { } observed && RemoteCharacter(save) is { } character)
                            _desktopSync.AcceptObservedMetadata(save, observed, character);
                        await UploadDesktopSaveAsync(save, manual); updated++;
                        break;
                    case DesktopCloudReconciliationAction.Download:
                        await TakeRemoteUpdateAsync(save.Id, false); updated++;
                        break;
                    case DesktopCloudReconciliationAction.Conflict:
                        if (manual && RemoteSave(save.Id) is { } roleCloud
                            && (roleCloud["isMain"]?.GetValue<bool>() == true) != save.IsMain
                            && !binding.LocalMainAsCloudSecondary)
                            throw new DesktopCloudConflictException("本地与云端主副档关系不同。采用云端内容请点“采用云端更新”；要以当前档作为云主档，请打开主档开关。");
                        await TakeRemoteUpdateAsync(save.Id, true);
                        if (manual) { await UploadDesktopSaveAsync(CurrentLocalSave(save.Id), true); updated++; }
                        else
                        {
                            _desktopSync.RecordFailure(save.Id, decision.Message + " 双方版本已保留在本地历史中，请手动处理。");
                            binding.Status = "conflict"; _desktopSync.Save();
                        }
                        break;
                    case DesktopCloudReconciliationAction.RecoverPending:
                        if (manual)
                        {
                            await TakeRemoteUpdateAsync(save.Id, true);
                            await UploadDesktopSaveAsync(CurrentLocalSave(save.Id), true); updated++;
                        }
                        break;
                }
            }
            catch (Exception error) when (IsDesktopError(error))
            {
                _desktopSync.RecordFailure(save.Id, error.Message);
                if (IsCloudVersionConflict(error)) { binding.Status = "conflict"; _desktopSync.Save(); }
                if (manual) throw;
            }
        }
        if (manual) StatusText.Text = updated > 0 ? $"已更新 {updated} 份云端存档。" : "已检查云端存档。";
    }

    private async Task RecoverDesktopOperationsAsync(string? localSaveId = null, JsonObject? remoteRole = null)
    {
        if (_desktopSync is null) return;
        var local = localSaveId is null ? null : _simulation?.FindSave(CloudSimulation.PreviewOwnerId, localSaveId);
        bool Related(DesktopCloudOperation operation) => localSaveId is null && remoteRole is null
            || localSaveId is not null && operation.LocalSaveId == localSaveId
            || local is not null && operation.GameAccount == local.GameAccount && operation.ProfessionId == _desktopSync.ProfessionId(local)
            || remoteRole is not null && operation.GameAccount == remoteRole["gameAccount"]?.ToString() && operation.ProfessionId == remoteRole["professionId"]?.ToString();
        foreach (var committed in _desktopSync.Account.CommittedMainTransfers.Where(Related).ToList()) ApplyCommittedRemoteMain(committed);
        foreach (var operation in _desktopSync.Account.PendingOperations.Where(Related).ToList())
        {
            if (_desktopSync.GetBinding(operation.LocalSaveId).StopRequestedByIgnore
                && !(operation.Kind == "sync-state" && JsonNode.Parse(operation.BodyJson)?["enabled"]?.GetValue<bool>() == false))
            {
                await _desktopSync.RecoverIgnoredOperationAsync(operation, SendDesktopAsync, _desktopLifetime.Token);
                continue;
            }
            if ((operation.Status is "conflict" or "failed" or "rejected") && !operation.MustQueryReceipt) continue;
            try { await _desktopSync.ExecuteAsync(operation, SendDesktopAsync, _desktopLifetime.Token, recoverTerminalReceipt: true); }
            catch (DesktopApiException) when ((operation.Status is "conflict" or "failed" or "rejected") && !operation.MustQueryReceipt)
            {
                await ReadDesktopCloudAsync();
                continue;
            }
            if (operation.Kind == "main-switch") ApplyCommittedRemoteMain(operation);
            await ReadDesktopCloudAsync();
        }
    }

    private async Task<DesktopCloudOperation?> ExecuteDesktopWriteAsync(CloudSave save, DesktopCloudOperation operation,
        bool manual, Func<DesktopCloudOperation> recreate)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _desktopSync!.ExecuteAsync(operation, SendDesktopAsync, _desktopLifetime.Token);
                return operation;
            }
            catch (DesktopApiException error) when (manual && error is { Code: "save_deleted", StatusCode: 409, OutcomeUnknown: false }
                && !operation.MustQueryReceipt && DesktopCloudSync.IsKnownFailedOperation(operation) && attempt < 8)
            {
                // Deleted slot IDs stay reserved on the server. Only a definite rejection permits a new request and slot.
                await ReadDesktopCloudAsync();
                if (await ChooseExistingCloudForEnableAsync(CurrentLocalSave(save.Id))) return null;
                _desktopSync!.RetireKnownFailedOperations(save);
                _desktopSync.PrepareCloudReenable(save, _remoteSaves, LiveLocalSaveIds(), confirmedDeletedByServer: true);
                _desktopSync.SetEnabled(save.Id, true);
                operation = recreate();
            }
            catch (DesktopApiException error) when (manual && error is { Code: "cloud_revision_conflict" or "character_revision_conflict", StatusCode: 409, OutcomeUnknown: false }
                && !operation.MustQueryReceipt && DesktopCloudSync.IsKnownFailedOperation(operation)
                && JsonNode.Parse(operation.BodyJson)?["expectedCloudRevision"]?.GetValue<long>() == 0)
            {
                await ReadDesktopCloudAsync();
                var createdElsewhere = _desktopSync!.FindUniqueCloudSaveByIdentity(save, _remoteSaves);
                if (createdElsewhere is null) throw;
                await LinkLocalToCloudAsync(save.Id, createdElsewhere["saveId"]!.ToString(), true);
                return null;
            }
        }
    }

    private async Task<bool> UploadDesktopSaveAsync(CloudSave save, bool manual)
    {
        if (_desktopSync is null) throw new InvalidOperationException("云资料尚未就绪。");
        DesktopCloudOperation op;
        try { op = _desktopSync.PrepareUpload(save, RemoteSave(save.Id), RemoteCharacter(save), allCloudSaves: _remoteSaves); }
        catch (Exception error) when (manual && IsCloudVersionConflict(error))
        {
            if (!ConfirmCloudConflict(save)) throw;
            op = _desktopSync.PrepareUpload(save, RemoteSave(save.Id), RemoteCharacter(save), acceptRemoteChanges: true, allCloudSaves: _remoteSaves);
        }
        try
        {
            var committed = await ExecuteDesktopWriteAsync(save, op, manual, () => _desktopSync.PrepareUpload(save, RemoteSave(save.Id),
                RemoteCharacter(save), allCloudSaves: _remoteSaves));
            await ReadDesktopCloudAsync();
            return committed is not null;
        }
        catch (DesktopApiException error) when (manual && IsCloudVersionConflict(error))
        {
            await ReadDesktopCloudAsync();
            if (!ConfirmCloudConflict(save)) throw;
            var replacement = _desktopSync.PrepareUpload(save, RemoteSave(save.Id), RemoteCharacter(save), acceptRemoteChanges: true, allCloudSaves: _remoteSaves);
            var committed = await ExecuteDesktopWriteAsync(save, replacement, manual, () => _desktopSync.PrepareUpload(save, RemoteSave(save.Id),
                RemoteCharacter(save), acceptRemoteChanges: true, allCloudSaves: _remoteSaves));
            await ReadDesktopCloudAsync();
            return committed is not null;
        }
    }

    private static bool IsCloudVersionConflict(Exception error) => error is DesktopApiException { Code: "cloud_revision_conflict" or "character_revision_conflict" or "client_revision_conflict" }
        || error is DesktopCloudConflictException;

    private async Task StopRemoteSyncAsync(CloudSave save, bool manual)
    {
        try
        {
            var op = _desktopSync!.PrepareSyncState(save.Id, RemoteSave(save.Id)!, false, acceptRemoteChanges: manual);
            await _desktopSync.ExecuteAsync(op, SendDesktopAsync, _desktopLifetime.Token);
        }
        catch (Exception error) when (manual && IsCloudVersionConflict(error))
        {
            await ReadDesktopCloudAsync();
            var latest = RemoteSave(save.Id) ?? throw new InvalidOperationException("云档已不在列表中，本地已停止自动上传。");
            if (MessageBox.Show(this, $"“{save.Name}”的云端状态已变化。是否仍要停止这份云档的同步？云端内容保留。",
                "确认停止同步", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) throw;
            var replacement = _desktopSync!.PrepareSyncState(save.Id, latest, false, acceptRemoteChanges: true);
            await _desktopSync.ExecuteAsync(replacement, SendDesktopAsync, _desktopLifetime.Token);
        }
        await ReadDesktopCloudAsync();
    }

    private bool ConfirmCloudConflict(CloudSave save) => MessageBox.Show(this,
        $"“{save.Name}”对应的云端存档或角色已变化。\n\n可以先取消并采用云端更新，双方旧版本都会保留在本地历史。是否使用当前本地内容再次提交？",
        "处理云端变化", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task SetRemoteMainAsync()
    {
        var save = SelectedLocalSave();
        if (_desktopSync is null) await ReadDesktopCloudAsync();
        await RecoverDesktopOperationsAsync(save.Id);
        await ReadDesktopCloudAsync();
        var binding = _desktopSync!.GetBinding(save.Id);
        if (binding.CloudRevision == 0 && _desktopSync.FindUniqueCloudSaveByIdentity(save, _remoteSaves) is { } sameIdentity)
        {
            await LinkLocalToCloudAsync(save.Id, sameIdentity["saveId"]!.ToString(), true);
            save = SelectedLocalSave();
        }
        _desktopSync!.RetireKnownFailedOperations(save);
        var character = RemoteCharacter(save);
        if (character is null)
        {
            _simulation!.SetMain(save.OwnerId, save.Id);
            FollowSelectedSaveTarget();
            if (RemoteSyncEnabled(save.Id)) await UploadDesktopSaveAsync(_simulation.FindSave(save.OwnerId, save.Id)!, true);
            return;
        }
        if (character["mainSaveId"]?.ToString() == _desktopSync!.GetBinding(save.Id).SaveId)
        {
            if (!save.IsMain)
                _simulation!.SetMain(save.OwnerId, save.Id);
            FollowSelectedSaveTarget();
            if (RemoteSyncEnabled(save.Id)) await UploadDesktopSaveAsync(CurrentLocalSave(save.Id), true);
            return;
        }
        if (!await ConfirmRemoteSharingAsync()) return;
        var successor = save;
        if (character["mainSaveId"] is null && !save.IsMain)
        {
            successor = JsonSerializer.Deserialize<CloudSave>(JsonSerializer.Serialize(save))!;
            successor.IsMain = true;
        }
        DesktopCloudOperation op;
        try { op = _desktopSync!.PrepareMainSwitch(successor, character, RemoteSave(save.Id), allCloudSaves: _remoteSaves); }
        catch (Exception error) when (IsCloudVersionConflict(error))
        {
            if (!ConfirmCloudConflict(save)) throw;
            op = _desktopSync!.PrepareMainSwitch(successor, character, RemoteSave(save.Id), acceptRemoteChanges: true, allCloudSaves: _remoteSaves);
        }
        var committed = await ExecuteDesktopWriteAsync(successor, op, true, () => _desktopSync.PrepareMainSwitch(successor,
            RemoteCharacter(save) ?? throw new InvalidOperationException("云端角色已变化，请刷新后重试。"),
            RemoteSave(save.Id), allCloudSaves: _remoteSaves));
        if (committed is null) return;
        var needsGoalUpload = ApplyCommittedRemoteMain(committed);
        await ReadDesktopCloudAsync();
        FollowSelectedSaveTarget();
        // The current server requires carrying its former main's public goals during the switch.
        // Keep each local save's own goals, then publish the selected main through the normal PUT.
        if (needsGoalUpload && RemoteSyncEnabled(save.Id))
        {
            StatusText.Text = "主档已切换，正在上传该档自己的目标和材料缺口……";
            try
            {
                if (!await UploadDesktopSaveAsync(CurrentLocalSave(save.Id), true)) return;
                var accepted = _desktopSync.GetBinding(save.Id);
                needsGoalUpload = !accepted.AcceptedIsMain
                    || _desktopSync.GetFingerprint(CurrentLocalSave(save.Id)) != accepted.AcceptedLocalFingerprint;
            }
            catch (Exception error) when (IsDesktopError(error))
            {
                throw new InvalidOperationException("主档已切换，该档目标尚未确认上传；本地各档目标均保留，请稍后更新云端存档。" + error.Message, error);
            }
        }
        StatusText.Text = needsGoalUpload ? "已切换主档，各档目标均保留；当前档目标尚待上传，请检查云同步后更新。"
            : "已切换主档并上传该档的目标和材料缺口；其他档的目标仍保留在本机。";
    }

    private bool ApplyCommittedRemoteMain(DesktopCloudOperation operation)
    {
        if (_simulation?.FindSave(CloudSimulation.PreviewOwnerId, operation.LocalSaveId) is not { } save) return false;
        if (save.IsIgnored || _desktopSync!.GetBinding(save.Id).StopRequestedByIgnore)
        { _desktopSync!.CompleteLocalMainTransfer(operation.RequestId); return false; }
        _simulation.SetMain(save.OwnerId, save.Id);
        return _desktopSync!.CompleteLocalMainTransfer(operation.RequestId, CurrentLocalSave(save.Id));
    }

    private async Task FinishIgnoredCloudStopsAsync()
    {
        if (_desktopSync is null || _simulation is null) return;
        foreach (var binding in _desktopSync.Account.Bindings.Where(x => x.StopRequestedByIgnore && !x.Deleted).ToList())
        {
            try
            {
                if (!_desktopSync.Account.PendingOperations.Any(x => x.LocalSaveId == binding.LocalSaveId)
                    && !_desktopSync.Account.CommittedMainTransfers.Any(x => x.LocalSaveId == binding.LocalSaveId)
                    && RemoteSave(binding.LocalSaveId)?["syncEnabled"]?.GetValue<bool>() != true)
                {
                    var changed = binding.AcceptedSyncEnabled || binding.LastError.Length > 0 || binding.Status != "disabled";
                    binding.AcceptedSyncEnabled = false; binding.LastError = ""; binding.Status = "disabled";
                    if (changed) _desktopSync.Save();
                    continue;
                }
                var unresolved = false;
                foreach (var operation in _desktopSync.Account.PendingOperations.Where(x => x.LocalSaveId == binding.LocalSaveId).ToList())
                {
                    if (operation.Kind == "sync-state" && JsonNode.Parse(operation.BodyJson)?["enabled"]?.GetValue<bool>() == false)
                    {
                        if (DesktopCloudSync.IsKnownFailedOperation(operation) && !operation.MustQueryReceipt)
                            _desktopSync.DiscardFailedOperation(operation.RequestId);
                        else await _desktopSync.ExecuteAsync(operation, SendDesktopAsync, _desktopLifetime.Token, recoverTerminalReceipt: true);
                    }
                    else if (!await _desktopSync.RecoverIgnoredOperationAsync(operation, SendDesktopAsync, _desktopLifetime.Token)) unresolved = true;
                }
                foreach (var operation in _desktopSync.Account.CommittedMainTransfers.Where(x => x.LocalSaveId == binding.LocalSaveId).ToList())
                    _desktopSync.CompleteLocalMainTransfer(operation.RequestId);
                if (unresolved) continue;
                await ReadDesktopCloudAsync();
                var remote = RemoteSave(binding.LocalSaveId);
                if (remote is null || remote["syncEnabled"]?.GetValue<bool>() == false)
                { binding.AcceptedSyncEnabled = false; binding.LastError = ""; binding.Status = "disabled"; _desktopSync.Save(); continue; }
                var stop = _desktopSync.PrepareSyncState(binding.LocalSaveId, remote, false, acceptRemoteChanges: true);
                await _desktopSync.ExecuteAsync(stop, SendDesktopAsync, _desktopLifetime.Token);
                await ReadDesktopCloudAsync();
            }
            catch (Exception error) when (IsDesktopError(error))
            { _desktopSync.RecordFailure(binding.LocalSaveId, "本机已停止同步，云端停止状态待确认：" + error.Message); }
        }
    }

    private static List<CloudGoal> ReadRemoteGoals(JsonArray goals) => goals.Select(n => new CloudGoal
        { ItemId = n!["itemId"]!.ToString(), ItemName = n["name"]!.ToString(), Quantity = checked((int)n["quantity"]!.GetValue<long>()) }).ToList();

    private async Task DownloadRemoteSaveAsync()
    {
        if (CloudCopiesBox.SelectedItem is not CloudCopyChoice choice) throw new InvalidOperationException("请选择云端存档。");
        if (!await ConfirmRemoteSharingAsync()) return;
        await ReadDesktopCloudAsync();
        var bound = _desktopSync!.Account.Bindings.FirstOrDefault(binding => binding.SaveId == choice.Id && !binding.Deleted
            && !binding.LocalSaveId.StartsWith("remote:", StringComparison.Ordinal)
            && _simulation!.FindSave(CloudSimulation.PreviewOwnerId, binding.LocalSaveId) is not null);
        if (bound is not null)
        {
            await LinkLocalToCloudAsync(bound.LocalSaveId, choice.Id, true);
            return;
        }
        var content = await SendDesktopAsync("GET", $"/api/desktop/cloud-saves/{Guid.Parse(choice.Id):D}/content", null, _desktopLifetime.Token);
        if (content["importAs"]?.ToString() != "secondary" || content["codes"] is not JsonArray codes || _decoder is null)
            throw new InvalidOperationException("云存档内容不完整，未修改本地存档。");
        var account = content["gameAccount"]!.GetValue<string>();
        var codeList = codes.Select(c => c!.GetValue<string>()).ToList();
        // Parse privately in memory; downloaded codes never overwrite a game/source TXT.
        var result = await Task.Run(() => new SaveDecoder().ParseBytesForImport(
            Encoding.UTF8.GetBytes(string.Join('\n', codeList)), "云端存档", account));
        var profession = result["hero"]?["name"]?.ToString() ?? "";
        if (profession != RemoteProfessionName(content["professionId"]?.ToString()))
            throw new InvalidOperationException("云存档的职业与代码不一致，未导入。");
        var inventory = new List<CloudInventoryItem>();
        foreach (var group in new[] { "inventory", "bag", "storage" })
            if (result["inventories"]?[group] is JsonArray items)
                inventory.AddRange(items.Select(i => new CloudInventoryItem { ItemId = i!["rawcode"]!.ToString(),
                    ItemName = i["name"]?.ToString() ?? "未命名物品", Quantity = i["quantity"]?.GetValue<int>() ?? 1 }));
        var name = content["saveName"]!.ToString();
        var local = _simulation!.State.Saves.SingleOrDefault(save => save.OwnerId == CloudSimulation.PreviewOwnerId
            && save.GameAccount == account && save.Profession == profession
            && string.Equals(save.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        local ??= _simulation.ImportCloudContent(CloudSimulation.PreviewOwnerId, new CloudImportSave
        {
            Name = name, GameAccount = account, Profession = profession,
            MapVersion = result["map_profile"]!["name"]!.ToString(), SourceHash = result["source"]!["sha256"]!.ToString(),
            ParsedResult = result, LoadCodes = codeList, Inventory = inventory
        });
        RefreshSimulationContext(false);
        await LinkLocalToCloudAsync(local.Id, choice.Id, true);
    }

    private async Task UnpublishRemoteMainAsync()
    {
        var save = SelectedLocalSave();
        if (_desktopSync is null) await ReadDesktopCloudAsync();
        await RecoverDesktopOperationsAsync(save.Id);
        await ReadDesktopCloudAsync();
        var remote = RemoteSave(save.Id) ?? throw new InvalidOperationException("请先完成这份存档的云同步。");
        if (remote["isMain"]?.GetValue<bool>() != true) return;
        var character = RemoteCharacter(save) ?? throw new InvalidOperationException("请先刷新云端角色信息。");
        _desktopSync!.RetireKnownFailedOperations(save);
        var operation = _desktopSync.PrepareMainUnpublish(save, remote, character);
        try { await _desktopSync.ExecuteAsync(operation, SendDesktopAsync, _desktopLifetime.Token); }
        catch (DesktopApiException error) when (error.StatusCode == 404 && error.Code != "save_not_found")
        { throw new InvalidOperationException("云服务尚未提供关闭主档上报的接口，需要服务器更新。本地目标、历史和云存档均保留。", error); }
        await ReadDesktopCloudAsync();
        StatusText.Text = "已撤回公开目标和材料需求；云存档继续同步，本地目标及历史保留。";
    }

    private async Task DeleteRemoteSaveAsync(bool confirmedForAutomation = false)
    {
        if (CloudCopiesBox.SelectedItem is not CloudCopyChoice choice) throw new InvalidOperationException("请选择云端存档。");
        var remote = _remoteSaves.Single(s => s["saveId"]?.ToString() == choice.Id);
        if (!(App.AutomationMode && confirmedForAutomation) && MessageBox.Show(this, $"删除云端“{remote["saveName"]}”？本地存档仍然保留。", "删除云端存档",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RecoverDesktopOperationsAsync(remoteRole: remote);
        try
        {
            var op = _desktopSync!.PrepareDelete(remote, acceptRemoteChanges: true);
            await _desktopSync.ExecuteAsync(op, SendDesktopAsync, _desktopLifetime.Token);
        }
        catch (Exception error) when (IsCloudVersionConflict(error))
        {
            await ReadDesktopCloudAsync();
            var latest = _remoteSaves.SingleOrDefault(s => s["saveId"]?.ToString() == choice.Id)
                ?? throw new InvalidOperationException("这份云档已不在列表中，本地存档保留。");
            if (MessageBox.Show(this, $"“{latest["saveName"]}”的云端状态已变化。是否仍然删除最新的云端存档？本地文件保留。",
                "确认删除最新云档", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) throw;
            var replacement = _desktopSync!.PrepareDelete(latest, acceptRemoteChanges: true);
            await _desktopSync.ExecuteAsync(replacement, SendDesktopAsync, _desktopLifetime.Token);
        }
        await ReadDesktopCloudAsync();
        StatusText.Text = "已删除云端存档，本地文件保持不变。";
    }
}
