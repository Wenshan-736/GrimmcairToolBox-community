using System.Text.Json.Nodes;
using System.Windows;
using Grimmcair.Desktop.Services;
using Grimmcair.Desktop.Views;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private bool SameRemoteRole(CloudSave local, JsonObject remote) =>
        remote["gameAccount"]?.ToString() == local.GameAccount && remote["professionId"]?.ToString() == _desktopSync!.ProfessionId(local);

    private string RemoteChoiceLabel(JsonObject remote, int duplicate = 0)
    {
        var id = remote["saveId"]!.ToString();
        var role = remote["isMain"]?.GetValue<bool>() == true ? "云主档" : "云副档";
        var linked = _desktopSync?.Account.Bindings.Any(binding => binding.SaveId == id && !binding.Deleted && binding.CloudRevision > 0 && !binding.LocalSaveId.StartsWith("remote:", StringComparison.Ordinal)) == true;
        var timestamp = DateTimeOffset.TryParse(remote["syncedAt"]?.ToString(), out var time)
            ? time.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss") : "时间待确认";
        return $"{remote["gameAccount"]} · {RemoteProfessionName(remote["professionId"]?.ToString())} · {remote["saveName"]}"
            + (duplicate > 0 ? $"（同名{duplicate}）" : "") + $" · {role} · {timestamp}" + (linked ? " · 已关联本机" : "");
    }

    private async Task<bool> ChooseExistingCloudForEnableAsync(CloudSave local)
    {
        if (_desktopSync!.GetBinding(local.Id).CloudRevision > 0 && RemoteSave(local.Id) is not null) return false;
        var choices = _remoteSaves.Where(remote => SameRemoteRole(local, remote)
                && string.Equals(remote["saveName"]?.ToString()?.Trim(), local.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(remote => remote["isMain"]?.GetValue<bool>() == true)
            .ThenByDescending(remote => remote["syncedAt"]?.ToString(), StringComparer.Ordinal).ToList();
        if (choices.Count == 0) return false;
        if (choices.Count == 1)
        {
            await LinkLocalToCloudAsync(local.Id, choices[0]["saveId"]!.ToString(), enableSync: true);
            return true;
        }
        if (App.AutomationMode) throw new DesktopCloudConflictException("旧云列表有多个同名档，需先明确选择原云档。");
        var dialog = new DesktopCloudLinkWindow($"当前本地：{local.Name}（{(local.IsMain ? "主档" : "副档")}）",
            choices.Select((remote, index) => new DesktopCloudLinkChoice(remote["saveId"]!.ToString(), RemoteChoiceLabel(remote, index + 1))).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true) throw new OperationCanceledException();
        await LinkLocalToCloudAsync(local.Id, dialog.SelectedSaveId!, enableSync: true);
        return true;
    }

    private async Task<bool> ChooseExistingLocalForEnableAsync(CloudSave local)
    {
        if (RemoteSave(local.Id) is not null) return false;
        var candidates = _simulation!.State.Saves.Where(other => other.OwnerId == local.OwnerId && !other.IsDemo && other.Id != local.Id
            && other.GameAccount == local.GameAccount && other.Profession == local.Profession
            && string.Equals(other.Name.Trim(), local.Name.Trim(), StringComparison.OrdinalIgnoreCase)
            && _desktopSync!.Account.Bindings.Any(binding => binding.LocalSaveId == other.Id)).ToList();
        if (candidates.Count == 0) return false;
        if (candidates.Count != 1) throw new InvalidOperationException("本机有多份同名旧记录，请先在存档列表选择原来同步的那一份。未修改任何存档。");
        var original = candidates[0];
        var document = _saves.SingleOrDefault(row => row.SimulationSaveId == original.Id)
            ?? throw new InvalidOperationException("原有关联档尚未载入，请刷新存档列表后重试。");
        SelectSaveDocument(document);
        if (_desktopSync!.GetFingerprint(original) != _desktopSync.GetFingerprint(local))
        {
            StatusText.Text = "已选择原有关联档；两份同名本地记录内容不同，均已保留。请核对当前内容后再开启同步。";
            if (!App.AutomationMode) MessageBox.Show(this, StatusText.Text, "本地同名存档", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        await SetRemoteSyncAsync(true, reuseLocalBinding: false);
        StatusText.Text = "已选择本机原有关联档并检查同步；同名记录没有覆盖或新增云槽。";
        return true;
    }

    private void LinkCloud_Click(object sender, RoutedEventArgs e) => _ = RunDesktopActionAsync(async () =>
    {
        var local = SelectedLocalSave();
        if (CloudCopiesBox.SelectedItem is not CloudCopyChoice choice) throw new InvalidOperationException("先选择需要关联的云档。");
        await ReadDesktopCloudAsync();
        var remote = _remoteSaves.SingleOrDefault(row => row["saveId"]?.ToString() == choice.Id)
            ?? throw new InvalidOperationException("所选云档已不在列表，请重新选择。");
        if (!SameRemoteRole(local, remote)) throw new InvalidOperationException("当前本地档与所选云档不是同一个角色，请先选择对应的本地档。");
        var previous = RemoteSave(local.Id);
        var replacement = previous is not null && previous["saveId"]?.ToString() != choice.Id
            ? $"\n\n将解除本机与旧云档“{previous["saveName"]}”的关联，旧云档仍保留，可随后手动删除。" : "";
        if (MessageBox.Show(this, $"将本地“{local.Name}”关联到以下云档并开始同步？\n\n{RemoteChoiceLabel(remote)}\n\n会取回云端最新内容，当前本地版本先保留在历史中，不增加云槽。{replacement}",
                "关联现有云档", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!await ConfirmRemoteSharingAsync()) return;
        await LinkLocalToCloudAsync(local.Id, choice.Id, true);
    });

    private async Task LinkLocalToCloudAsync(string localId, string remoteId, bool enableSync)
    {
        var sync = _desktopSync ?? throw new InvalidOperationException("云同步尚未就绪。");
        var existing = sync.Account.Bindings.FirstOrDefault(binding => binding.SaveId == remoteId && !binding.Deleted
            && binding.LocalSaveId != localId && !binding.LocalSaveId.StartsWith("remote:", StringComparison.Ordinal));
        if (existing is not null && _simulation?.FindSave(CloudSimulation.PreviewOwnerId, existing.LocalSaveId) is not null)
        {
            SelectSaveDocument(_saves.Single(document => document.SimulationSaveId == existing.LocalSaveId));
            sync.SetEnabled(existing.LocalSaveId, enableSync);
            await RecoverDesktopOperationsAsync(existing.LocalSaveId);
            await ReadDesktopCloudAsync();
            if (enableSync && RemoteSave(existing.LocalSaveId) is { } stopped && stopped["syncEnabled"]?.GetValue<bool>() == false)
            {
                var resume = sync.PrepareSyncState(existing.LocalSaveId, stopped, true, acceptRemoteChanges: true);
                await sync.ExecuteAsync(resume, SendDesktopAsync, _desktopLifetime.Token);
                await ReadDesktopCloudAsync();
            }
            RefreshSimulationContext(false);
            QueueDesktopSync();
            StatusText.Text = "这个云档已关联本机，已选择原有关联档并检查同步，没有覆盖本地内容或新增云槽。";
            return;
        }
        await RecoverDesktopOperationsAsync(localId);
        await ReadDesktopCloudAsync();
        var local = CurrentLocalSave(localId);
        var listed = _remoteSaves.SingleOrDefault(row => row["saveId"]?.ToString() == remoteId)?.DeepClone().AsObject()
            ?? throw new InvalidOperationException("所选云档已删除，本地档与历史保留。");
        var listedCharacter = RemoteCharacter(local)?.DeepClone().AsObject()
            ?? throw new InvalidOperationException("云端角色资料未就绪，请刷新后重试。");
        var content = await SendDesktopAsync("GET", $"/api/desktop/cloud-saves/{Guid.Parse(remoteId):D}/content", null, _desktopLifetime.Token);
        await ReadDesktopCloudAsync();
        sync.RetireOrphanedBinding(local, remoteId, LiveLocalSaveIds());
        var snapshot = sync.PrepareLinkSnapshot(local, listed, listedCharacter, content,
            _remoteSaves.SingleOrDefault(row => row["saveId"]?.ToString() == remoteId) ?? throw new InvalidOperationException("读取期间云档已删除。"),
            RemoteCharacter(local) ?? throw new InvalidOperationException("读取期间云端角色已变化。"), replaceExistingBinding: true);
        var import = await DecodeDesktopSnapshotAsync(snapshot);
        if (_closing || !DesktopFeaturesAllowed || _desktopSync != sync) throw new OperationCanceledException();
        local = CurrentLocalSave(localId);
        sync.ValidateLinkedSnapshotForApply(local, snapshot);
        _refreshingSimulation = true;
        CloudSave applied;
        try
        {
            if (snapshot.IsMain && !local.IsMain)
                _simulation!.ApplyRemoteMain(local.OwnerId, local.Id, snapshot.Goals);
            else if (!snapshot.IsMain && local.IsMain)
            {
                var other = _simulation!.State.Saves.FirstOrDefault(save => save.OwnerId == local.OwnerId && save.GameAccount == local.GameAccount
                    && save.Profession == local.Profession && save.Id != local.Id && !save.IsDemo);
                if (other is not null) throw new InvalidOperationException("当前本地主档不能自动降为云副档。请先明确选择另一份本地主档，或关联云主档。");
            }
            var beforeApply = CurrentLocalSave(localId);
            applied = _simulation!.ApplyRemoteSnapshot(local.OwnerId, local.Id, import,
                beforeApply.IsMain && !snapshot.IsMain ? null : snapshot.Goals);
            sync.AcceptLinkedSnapshot(applied, snapshot);
        }
        finally { _refreshingSimulation = false; }
        if (enableSync && !snapshot.SyncEnabled)
        {
            sync.SetEnabled(applied.Id, true);
            var latest = RemoteSave(applied.Id) ?? throw new InvalidOperationException("关联云档已不在列表。");
            var resume = sync.PrepareSyncState(applied.Id, latest, true, acceptRemoteChanges: true);
            await sync.ExecuteAsync(resume, SendDesktopAsync, _desktopLifetime.Token);
            await ReadDesktopCloudAsync();
        }
        RefreshSimulationContext(false);
        SelectSaveDocument(_saves.Single(document => document.SimulationSaveId == applied.Id));
        StatusText.Text = applied.IsMain && !snapshot.IsMain
            ? "已关联原云槽；当前是本地主档、云端副档，本地目标不公开上报。可打开主档开关进行转换。"
            : "已关联原云槽并取回最新内容，旧版本保留在本地历史中，没有新增云档。";
    }

    private async Task UseRemoteSnapshotAsync()
    {
        var save = SelectedLocalSave();
        await RecoverDesktopOperationsAsync(save.Id);
        await ReadDesktopCloudAsync();
        var remote = RemoteSave(save.Id) ?? throw new InvalidOperationException("当前档没有可读取的绑定云档，请先选择云档并关联。");
        if (MessageBox.Show(this, $"采用“{remote["saveName"]}”的云端最新内容？\n\n当前本地版本先保留在历史中，原游戏TXT不变，云端不会被覆盖。",
            "采用云端更新", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await LinkLocalToCloudAsync(save.Id, remote["saveId"]!.ToString(), true);
    }
    private void UseRemoteSnapshot_Click(object sender, RoutedEventArgs e) => _ = RunDesktopActionAsync(UseRemoteSnapshotAsync);
}
