using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.Social;

namespace PCL;

public partial class HybridFriends : UserControl, IDisposable
{
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _lifetime = new();
    private FriendApiClient? _api;
    private FriendSession? _session;
    private string _profileId = "";
    private string _username = "";
    private string _uuid = "";
    private string _accessToken = "";
    private bool _premium;
    private bool _loaded;
    private bool _closed;
    private bool _privacyReady;
    private bool _notices = true;
    private string _tab = "online";
    private readonly string _cachePath;
    private int _generation;
    private List<Person> _people = [];
    private List<ChatRoom> _channels = [];
    private string? _channelId;
    private FriendSettings? _settings;
    private sealed record Person(string Uuid, string Name, string State, string Detail, string Kind, string? Server, string Playing)
    {
        public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    }
    private sealed record ChatRoom(string Id, string Title, bool Direct, string? Peer)
    {
        public string Initial => Id.StartsWith("invite:", StringComparison.Ordinal) ? "邀" : Direct ? "聊" : "群";
    }
    private sealed record ChatLine(string Sender, string Content, string Time, bool Mine);
    private bool _updatingSelection;
    private bool _openingChat;
    private bool _sending;
    private bool _requesting;
    private int _selectionVersion;
    private IInputElement? _overlayFocus;
    private string? _pendingRoomCode;
    private string? _pendingRoomName;
    private string? _promptedInviteKey;
    private string? _pendingJoinCode;



    public HybridFriends() : this(null) { }

    internal HybridFriends(string? settingsPath)
    {
        _settingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MOONCL", "friends.json");
        _cachePath = Path.Combine(Path.GetDirectoryName(_settingsPath)!, "friend-chats.json");
        InitializeComponent();
        LoadSettings();
        _loaded = true;
        ShowTab();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CloseOverlay();
        };
    }

    public void UpdateAccount(McProfile? profile)
    {
        if (_closed) return;
        var premium = profile is { ProfileType: ProfileType.Microsoft } && !string.IsNullOrWhiteSpace(profile.AccessToken);
        string uuid;
        try { uuid = premium ? FriendApiClient.MojangUuid(profile!.Uuid) : ""; }
        catch (ArgumentException) { premium = false; uuid = ""; }
        var changed = profile?.ProfileId != _profileId || uuid != _uuid || profile?.AccessToken != _accessToken;
        _profileId = profile?.ProfileId ?? "";
        _username = profile?.UserName ?? "";
        _uuid = uuid;
        _accessToken = premium ? profile!.AccessToken : "";
        _premium = premium;
        if (!changed) return;
        StopGateway();
        _session = null;
        _api?.Dispose();
        _api = null;
        ResetView();
        Identity.Text = !_premium ? "需要正版微软账号" : _username;
        Status.Text = _premium
            ? "已使用当前正版档案。打开本页会向好友服务登记一次 Mojang 会话。"
            : "离线、Authlib 和外置登录不能使用这套好友系统。";
        if (_premium) _ = ConnectAsync();
    }

    private async void Request_Click(object sender, RoutedEventArgs e)
    {
        if (_requesting) return;
        var target = Target.Text.Trim();
        var byName = !FriendApiClient.TryUuid(target, out _);
        if (byName && (target.Length is < 3 or > 16 || target.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
        {
            Status.Text = "请输入 3–16 位正版用户名（字母、数字、下划线）或有效 UUID。";
            Target.Focus();
            return;
        }
        _requesting = true;
        RequestButton.IsEnabled = Target.IsEnabled = false;
        RequestButton.Content = "正在发送…";
        try
        {
            if (await RelateAsync("request", byName, target).ConfigureAwait(true))
            {
                Target.Clear();
                _tab = "pending";
                Search.Clear();
                ShowTab();
            }
        }
        finally
        {
            _requesting = false;
            RequestButton.IsEnabled = Target.IsEnabled = true;
            RequestButton.Content = "发送好友申请";
        }
    }
    private async void Accept_Click(object sender, RoutedEventArgs e) => await RelateSelectedAsync("friend");
    private async void Remove_Click(object sender, RoutedEventArgs e) => await RelateSelectedAsync("none");
    private async void Block_Click(object sender, RoutedEventArgs e) => await RelateSelectedAsync("blocked");
    private async Task RelateSelectedAsync(string relation)
    {
        if (People.SelectedItem is not Person person) { Status.Text = "请先在列表中选择一位玩家。"; return; }
        await RelateAsync(relation, false, person.Uuid).ConfigureAwait(true);
    }

    private async Task<bool> RelateAsync(string relation, bool byName, string raw)
    {
        if (!await EnsureSessionAsync().ConfigureAwait(true) || _session is null || _api is null) return false;
        try
        {
            var target = raw.Trim();
            if (byName)
            {
                Status.Text = "正在查询正版用户名…";
                target = await _api.LookupUuidAsync(target, _lifetime.Token).ConfigureAwait(true);
            }
            await _api.SetRelationAsync(_session.AccessToken, _session.Uuid, target, relation, _lifetime.Token).ConfigureAwait(true);
            if (relation == "request") RememberRequest(target);
            Status.Text = relation switch
            {
                "request" => "已发送好友申请，可在左侧「申请」中查看。对方单独接受即可成为好友。",
                "friend" => "已接受好友申请。",
                "blocked" => "已拉黑。",
                _ => "已删除、拒绝或取消。"
            };
            if (relation != "request") _requests.Remove(target);
            await RefreshAsync().ConfigureAwait(true);
            if (relation == "request") { _tab = "pending"; Search.Clear(); ShowTab(); }
            return true;
        }
        catch (Exception ex) { Status.Text = Message(ex); return false; }
    }

    private async Task<bool> EnsureSessionAsync()
    {
        if (!_premium) { Status.Text = "请先登录正版微软账号。"; return false; }
        if (_session is not null && _api is not null) return true;
        return await ConnectAsync().ConfigureAwait(true);
    }

    private async Task<bool> ConnectAsync()
    {
        if (!_premium || _closed) return false;
        var generation = ++_generation;
        var url = ServiceUrl();
        try
        {
            FriendApiClient.Normalize(url);
        }
        catch (ArgumentException ex) { Status.Text = ex.Message; return false; }
        Status.Text = "正在连接好友服务…";
        var api = new FriendApiClient(url);
        try
        {
            var session = await api.AuthenticateAsync(_username, _accessToken, _uuid, _lifetime.Token).ConfigureAwait(true);
            if (generation != _generation || _closed) { api.Dispose(); return false; }
            _api?.Dispose();
            _api = api;
            _session = session;
            Identity.Text = session.Username + (FriendApiClient.IsOfficial(url) ? " · 官方" : " · 自定义");
            await RefreshAsync().ConfigureAwait(true);
            StartGateway(url, session.AccessToken, generation);
            return true;
        }
        catch (Exception ex)
        {
            api.Dispose();
            if (generation == _generation) Status.Text = Message(ex);
            return false;
        }
    }

    private async Task RefreshAsync()
    {
        if (_api is null || _session is null) return;
        await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
        try
        {
            var token = _session.AccessToken;
            var friends = await _api.FriendsAsync(token, _lifetime.Token).ConfigureAwait(true);
            var requests = await _api.RequestsAsync(token, _lifetime.Token).ConfigureAwait(true);
            var blocked = await _api.BlockedAsync(token, _lifetime.Token).ConfigureAwait(true);
            var ids = friends.Concat(requests.Incoming).Concat(requests.Outgoing).Concat(blocked).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var names = new Dictionary<string, FriendProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                try { names[id] = await _api.UserAsync(token, id, _lifetime.Token).ConfigureAwait(true); }
                catch (FriendApiException) { names[id] = new FriendProfile(id, id, null, null, null, null); }
            }
            if (_closed) return;
            _people = friends.Select(id => Entry(names, id, "friend"))
                .Concat(requests.Incoming.Select(id => Entry(names, id, "incoming")))
                .Concat(requests.Outgoing.Select(id => Entry(names, id, "outgoing")))
                .Concat(blocked.Select(id => Entry(names, id, "blocked")))
                .OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase).ToList();
            RememberActivities(names);
            var online = _people.Count(person => person.Kind == "friend" && person.State == "在线");
            Identity.Text = (_session.Username) + (FriendApiClient.IsOfficial(ServiceUrl()) ? " · 官方" : " · 自定义") + " · 在线 " + online + "/" + friends.Count;
            ShowTab();
            _ = LoadChannelsAsync();
            _ = LoadPrivacyAsync();
            _ = PublishActivityAsync();
        }
        finally { _gate.Release(); }
    }

    private Person Entry(IReadOnlyDictionary<string, FriendProfile> names, string id, string kind)
    {
        names.TryGetValue(id, out var profile);
        var state = kind switch { "incoming" => "收到申请", "outgoing" => "已发出", "blocked" => "已拉黑", _ => profile?.Status == "online" ? "在线" : "离线" };
        var playing = string.Join(" · ", new[] { profile?.Activity, profile?.ActivityDescription, profile?.ServerName }.Where(text => !string.IsNullOrWhiteSpace(text)));
        var detail = profile?.Status == "online" ? (string.IsNullOrWhiteSpace(profile.ServerAddress) ? "正在游戏中" : profile.ServerAddress) : "未公开当前活动";
        return new Person(id, profile?.Username ?? id, state, detail, kind, FriendApiClient.ServerOf(profile)?.Address, string.IsNullOrWhiteSpace(playing) ? state : playing);
    }

    private sealed record Play(string Title, string Detail, DateTimeOffset Started)
    {
        public string When => Started.ToLocalTime().ToString("MM-dd HH:mm");
    }
    private sealed class ActivityStore { public Dictionary<string, List<Play>> Friends { get; set; } = new(StringComparer.OrdinalIgnoreCase); }

    private void RememberActivities(IReadOnlyDictionary<string, FriendProfile> names)
    {
        if (_session is null) return;
        var store = LoadActivities();
        var changed = false;
        foreach (var profile in names.Values.Where(profile => profile.Status == "online" && !string.IsNullOrWhiteSpace(profile.Activity)))
        {
            var key = FriendApiClient.MojangUuid(_session.Uuid) + ":" + profile.Uuid;
            if (!store.Friends.TryGetValue(key, out var plays)) store.Friends[key] = plays = [];
            var started = profile.Started ?? DateTimeOffset.UtcNow;
            var title = profile.Activity!.Trim();
            var detail = string.Join(" · ", new[] { profile.ActivityDescription, profile.ServerName, profile.ServerAddress }.Where(text => !string.IsNullOrWhiteSpace(text)));
            if (plays.Any(play => play.Title == title && play.Detail == detail && Math.Abs((play.Started - started).TotalMinutes) < 1)) continue;
            plays.Add(new Play(title, detail, started));
            store.Friends[key] = plays.OrderByDescending(play => play.Started).Take(100).ToList();
            changed = true;
        }
        if (changed) SaveActivities(store);
    }

    private string ActivityPath => Path.Combine(Path.GetDirectoryName(_cachePath)!, "friend-activity.json");

    private ActivityStore LoadActivities()
    {
        try
        {
            if (!File.Exists(ActivityPath) || new FileInfo(ActivityPath).Length > 2_000_000) return new ActivityStore();
            return JsonSerializer.Deserialize<ActivityStore>(File.ReadAllText(ActivityPath)) ?? new ActivityStore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new ActivityStore(); }
    }

    private void SaveActivities(ActivityStore store)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ActivityPath)!);
            File.WriteAllText(ActivityPath, JsonSerializer.Serialize(store));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private readonly Dictionary<string, string> _requests = new(StringComparer.OrdinalIgnoreCase);

    private void RememberRequest(string uuid)
    {
        var id = FriendApiClient.MojangUuid(uuid);
        _requests[id] = Target.Text.Trim();
        if (_people.Any(person => string.Equals(person.Uuid, id, StringComparison.OrdinalIgnoreCase) && person.Kind is "outgoing" or "friend")) return;
        _people.RemoveAll(person => string.Equals(person.Uuid, id, StringComparison.OrdinalIgnoreCase));
        _people.Add(new Person(id, string.IsNullOrWhiteSpace(_requests[id]) ? id : _requests[id], "已发出", "等待对方接受", "outgoing", null, "已发出"));
        _people = _people.OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ShowTab()
    {
        var rows = _tab switch
        {
            "all" => _people.Where(person => person.Kind == "friend"),
            "pending" => _people.Where(person => person.Kind is "incoming" or "outgoing"),
            "blocked" => _people.Where(person => person.Kind == "blocked"),
            _ => _people.Where(person => person.Kind == "friend" && person.State == "在线")
        };
        var search = Search.Text.Trim();
        var visible = rows.Where(person => person.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || person.Uuid.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = (People.SelectedItem as Person)?.Uuid;
        _updatingSelection = true;
        try
        {
            People.ItemsSource = visible;
            People.SelectedItem = visible.FirstOrDefault(person => person.Uuid == selected);
        }
        finally { _updatingSelection = false; }
        if (selected is not null && People.SelectedItem is null && _openingChat) ClearConversation();
        ListTitle.Text = _tab switch { "all" => "全部好友", "pending" => "好友申请", "blocked" => "已拉黑", _ => "在线好友" };
        PeopleCount.Text = visible.Length.ToString();
        PeopleEmpty.Text = search.Length > 0 ? "没有找到匹配的好友" : _tab switch
        {
            "all" => "还没有好友\n点击上方「添加」结识新伙伴",
            "pending" => "暂无好友申请",
            "blocked" => "没有已拉黑的玩家",
            _ => "暂无在线好友\n可以在「全部」中查看离线好友"
        };
        PaintTabs();
        var pending = _people.Count(person => person.Kind is "incoming" or "outgoing");
        PendingTab.Content = pending > 0 ? "申请 " + pending : "申请";
        UpdateActions();
    }

    private void PaintTabs()
    {
        foreach (var button in new[] { OnlineTab, AllTab, PendingTab, BlockedTab })
            button.Style = (Style)FindResource(Equals(button.Tag, _tab) ? "ActiveTab" : "Tab");
    }

    private void UpdateActions()
    {
        var person = People.SelectedItem as Person;
        var friend = person?.Kind == "friend";
        var incoming = person?.Kind == "incoming";
        RemoveButton.Visibility = Show(friend);
        ChatButton.Visibility = Show(friend && _channelId is null && !_openingChat);
        InviteRoomButton.Visibility = Show(friend && _pendingRoomCode is not null);
        JoinButton.Visibility = Show(friend && !string.IsNullOrWhiteSpace(person?.Server));
        BlockButton.Visibility = Show(friend);
        AcceptButton.Visibility = DenyButton.Visibility = Show(incoming);
        CancelButton.Visibility = Show(person?.Kind == "outgoing");
        UnblockButton.Visibility = Show(person?.Kind == "blocked");
        var invite = Channels.SelectedItem is ChatRoom room && room.Id.StartsWith("invite:", StringComparison.Ordinal);
        AcceptInviteButton.Visibility = IgnoreInviteButton.Visibility = Show(invite);
        LeaveButton.Visibility = Show(Channels.SelectedItem is ChatRoom open && !open.Id.StartsWith("invite:", StringComparison.Ordinal));
        var enabled = person is not null;
        RemoveButton.IsEnabled = ChatButton.IsEnabled = InviteRoomButton.IsEnabled = JoinButton.IsEnabled = BlockButton.IsEnabled = AcceptButton.IsEnabled = DenyButton.IsEnabled = CancelButton.IsEnabled = UnblockButton.IsEnabled = enabled;
        UpdateChatState();
    }

    private void UpdateChatState()
    {
        if (!_loaded) return;
        var person = People.SelectedItem as Person;
        var room = Channels.SelectedItem as ChatRoom;
        var invite = room?.Id.StartsWith("invite:", StringComparison.Ordinal) == true;
        ChatTitle.Text = person?.Name ?? room?.Title ?? "开始一段聊天";
        ChatSubtitle.Text = person is not null ? person.State + " · " + person.Detail
            : invite ? "收到频道邀请，接受后即可查看消息"
            : room is not null ? room.Direct ? "私聊 · 只有你和对方可以查看" : "频道聊天"
            : "从左侧选择好友或最近会话";
        ChatEmpty.Visibility = Show(!Messages.HasItems);
        ChatEmptyTitle.Text = _openingChat ? "正在打开私聊…" : invite ? "新的频道邀请"
            : _channelId is not null ? "还没有消息" : person is not null ? person.State : "与好友保持联系";
        ChatEmptyHint.Text = _openingChat ? "正在连接好友服务，请稍候。" : invite ? "点击上方「接受频道」加入聊天。"
            : _channelId is not null ? "发送第一条消息，打个招呼吧。"
            : person?.Kind == "incoming" ? "接受好友申请后，即可开始私聊。"
            : person?.Kind == "outgoing" ? "等待对方接受你的好友申请。"
            : person?.Kind == "blocked" ? "已拉黑此玩家，可以在上方取消拉黑。"
            : person is not null ? "点击上方「打开私聊」重新连接。"
            : "选择左侧好友即可私聊，也可以添加新的好友。";
        Composer.IsEnabled = _channelId is not null && !_openingChat && _session is not null;
        SendButton.IsEnabled = Composer.IsEnabled && !_sending && !string.IsNullOrWhiteSpace(Composer.Text);
    }

    private void ClearConversation()
    {
        _selectionVersion++;
        _openingChat = false;
        _channelId = null;
        _updatingSelection = true;
        try { Channels.SelectedItem = null; }
        finally { _updatingSelection = false; }
        Messages.ItemsSource = null;
        Composer.Clear();
        UpdateActions();
    }

    private void ResetView()
    {
        _people = [];
        _channels = [];
        _privacyReady = false;
        _settings = null;
        _updatingSelection = true;
        try { People.ItemsSource = Channels.ItemsSource = null; }
        finally { _updatingSelection = false; }
        ClearConversation();
        ShowTab();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loaded) ShowTab();
    }

    private void Composer_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loaded) UpdateChatState();
    }

    private void Composer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (SendButton.IsEnabled) Send_Click(sender, e);
    }

    private static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        _tab = ((Button)sender).Tag?.ToString() ?? "online";
        ShowTab();
    }

    private async void People_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _updatingSelection) return;
        if (People.SelectedItem is Person person) ShowActivityDetails(person);
        ClearConversation();
        if (People.SelectedItem is Person { Kind: "friend" } friend) await OpenDirectAsync(friend).ConfigureAwait(true);
    }

    private void ShowActivityDetails(Person person)
    {
        var plays = _session is null ? [] : LoadActivities().Friends.TryGetValue(FriendApiClient.MojangUuid(_session.Uuid) + ":" + person.Uuid, out var saved) ? saved.OrderByDescending(play => play.Started).ToArray() : [];
        ActivitySummary.Text = person.Playing;
        ActivityDetail.Text = person.State + " · " + person.Detail;
        ActivityHistory.ItemsSource = plays;
        ActivityEmpty.Visibility = Show(plays.Length == 0);
        OpenOverlay(ActivityPanel, person.Name + " 的动态", CloseOverlayButton);
    }

    private async void Chat_Click(object sender, RoutedEventArgs e)
    {
        if (People.SelectedItem is Person { Kind: "friend" } person) await OpenDirectAsync(person).ConfigureAwait(true);
    }

    private async Task OpenDirectAsync(Person person)
    {
        var selection = ++_selectionVersion;
        _openingChat = true;
        UpdateActions();
        try
        {
            if (!await EnsureSessionAsync().ConfigureAwait(true) || _api is null || _session is null || selection != _selectionVersion) return;
            Status.Text = "正在打开私聊…";
            var channel = await _api.GetOrCreateDirectAsync(_session.AccessToken, _session.Uuid, person.Uuid, _lifetime.Token).ConfigureAwait(true);
            if (selection != _selectionVersion || _closed) return;
            _channelId = channel;
            await LoadChannelsAsync().ConfigureAwait(true);
            if (selection != _selectionVersion) return;
            await LoadMessagesAsync().ConfigureAwait(true);
            if (selection == _selectionVersion) Status.Text = "已打开与 " + person.Name + " 的私聊。";
        }
        catch (Exception ex) { if (selection == _selectionVersion) Status.Text = Message(ex); }
        finally
        {
            if (selection == _selectionVersion)
            {
                _openingChat = false;
                UpdateActions();
            }
        }
    }

    private async void Channel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _updatingSelection) return;
        _selectionVersion++;
        _openingChat = false;
        _updatingSelection = true;
        try { People.SelectedItem = null; }
        finally { _updatingSelection = false; }
        _channelId = Channels.SelectedItem is ChatRoom room && !room.Id.StartsWith("invite:", StringComparison.Ordinal) ? room.Id : null;
        Messages.ItemsSource = null;
        Composer.Clear();
        UpdateActions();
        if (_channelId is not null) await LoadMessagesAsync().ConfigureAwait(true);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_sending || !SendButton.IsEnabled || _channelId is null || _api is null || _session is null) return;
        var channel = _channelId;
        var content = Composer.Text;
        var selection = _selectionVersion;
        _sending = true;
        SendButton.Content = "发送中…";
        UpdateChatState();
        try
        {
            await _api.SendMessageAsync(_session.AccessToken, channel, content, _session.Username, _lifetime.Token).ConfigureAwait(true);
            if (selection != _selectionVersion) return;
            if (Composer.Text == content) Composer.Clear();
            await LoadMessagesAsync().ConfigureAwait(true);
        }
        catch (Exception ex) { if (selection == _selectionVersion) Status.Text = Message(ex); }
        finally
        {
            _sending = false;
            SendButton.Content = "发送";
            UpdateChatState();
        }
    }
    private async void Leave_Click(object sender, RoutedEventArgs e)
    {
        if (_channelId is not { } channel || !await EnsureSessionAsync().ConfigureAwait(true) || _api is null || _session is null) return;
        try
        {
            await _api.LeaveChannelAsync(_session.AccessToken, channel, _lifetime.Token).ConfigureAwait(true);
            if (_channelId == channel) ClearConversation();
            await LoadChannelsAsync().ConfigureAwait(true);
            Status.Text = "已离开或删除频道。";
        }
        catch (Exception ex) { Status.Text = Message(ex); }
    }
    private async void AcceptInvite_Click(object sender, RoutedEventArgs e) => await AnswerInviteAsync(true);
    private async void IgnoreInvite_Click(object sender, RoutedEventArgs e) => await AnswerInviteAsync(false);
    private async Task AnswerInviteAsync(bool accept)
    {
        if (Channels.SelectedItem is not ChatRoom room || !room.Id.StartsWith("invite:", StringComparison.Ordinal) || !await EnsureSessionAsync().ConfigureAwait(true) || _api is null || _session is null) return;
        try
        {
            await _api.AnswerChannelInviteAsync(_session.AccessToken, room.Id["invite:".Length..], accept, _lifetime.Token).ConfigureAwait(true);
            if (Channels.SelectedItem is ChatRoom selected && selected.Id == room.Id) ClearConversation();
            if (accept) _channelId = room.Id["invite:".Length..];
            await LoadChannelsAsync().ConfigureAwait(true);
            if (accept) await LoadMessagesAsync().ConfigureAwait(true);
            Status.Text = accept ? "已加入频道。" : "已忽略频道邀请。";
        }
        catch (Exception ex) { Status.Text = Message(ex); }
    }
    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if (People.SelectedItem is not Person { Server: { } address } person || !FriendApiClient.IsJoinAddress(address)) { Status.Text = "这位好友没有公开可加入的服务器。"; return; }
        var choices = RoomPresentation.SameVersion(ModInstanceList.McMcInstanceSelected?.Info?.VanillaName);
        var selected = choices.FirstOrDefault(instance => ReferenceEquals(instance, ModInstanceList.McMcInstanceSelected)) ?? choices.FirstOrDefault() ?? ModInstanceList.McMcInstanceSelected;
        if (selected is null) { Status.Text = "请先在启动页选择一个游戏实例。"; return; }
        if (!ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions { instance = selected, ServerIp = address }))
            Status.Text = "启动器没有开始连接 " + address + "。";
        else Status.Text = "正在用 " + selected.Name + " 加入 " + person.Name + " 所在的 " + address + "。";
    }
    private async Task LoadChannelsAsync()
    {
        if (_api is not { } api || _session is not { } session) return;
        var generation = _generation;
        var token = _lifetime.Token;
        try
        {
            var ids = await api.ChannelIdsAsync(session.AccessToken, token).ConfigureAwait(true);
            var rooms = new List<ChatRoom>();
            foreach (var id in ids)
            {
                try
                {
                    var channel = await api.ChannelAsync(session.AccessToken, id, session.Uuid, token).ConfigureAwait(true);
                    var other = channel.Participants.FirstOrDefault(uuid => !string.Equals(uuid, session.Uuid, StringComparison.OrdinalIgnoreCase))
                        ?? (string.Equals(channel.Owner, session.Uuid, StringComparison.OrdinalIgnoreCase) ? null : channel.Owner);
                    var title = channel.Direct ? "私聊 · " + (_people.FirstOrDefault(person => string.Equals(person.Uuid, other, StringComparison.OrdinalIgnoreCase))?.Name ?? other ?? "好友") : channel.Name;
                    rooms.Add(new ChatRoom(channel.Id, title, channel.Direct, channel.Direct ? other : null));
                }
                catch (FriendApiException) { }
            }
            rooms = Dedupe(rooms);
            foreach (var invite in await api.ChannelInvitesAsync(session.AccessToken, token).ConfigureAwait(true))
                rooms.Add(new ChatRoom("invite:" + invite.ChannelId, "邀请 · " + (string.IsNullOrWhiteSpace(invite.Name) ? invite.ChannelId : invite.Name), false, null));
            if (generation != _generation || _closed) return;
            var selectedId = _channelId ?? (Channels.SelectedItem as ChatRoom)?.Id;
            _channels = rooms;
            _updatingSelection = true;
            try
            {
                Channels.ItemsSource = rooms;
                Channels.SelectedItem = rooms.FirstOrDefault(room => room.Id == selectedId);
            }
            finally { _updatingSelection = false; }
            if (_channelId is not null && Channels.SelectedItem is null && !_openingChat) ClearConversation();
            UpdateActions();
        }
        catch (Exception ex) { if (generation == _generation && !_closed) Status.Text = Message(ex); }
    }

    private async Task LoadMessagesAsync()
    {
        if (_session is not { } session || _channelId is not { } channel) return;
        var selection = _selectionVersion;
        var generation = _generation;
        ShowCached(channel, selection, generation);
        if (_api is not { } api) return;
        try
        {
            var messages = await api.MessagesAsync(session.AccessToken, channel, null, _lifetime.Token).ConfigureAwait(true);
            if (selection != _selectionVersion || generation != _generation || channel != _channelId || _closed) return;
            SaveCache(session.Uuid, channel, messages);
            ShowLines(messages.Select(ToLine).ToArray());
        }


        catch (Exception ex) { if (selection == _selectionVersion && generation == _generation && !_closed && !Messages.HasItems) Status.Text = Message(ex); }
    }

    private void ShowCached(string channel, int selection, int generation)
    {
        if (_session is null || selection != _selectionVersion || generation != _generation) return;
        var cached = ReadCache(_session.Uuid, channel);
        if (cached.Count > 0 && channel == _channelId) ShowLines(cached.Select(ToLine).ToArray());
    }

    private ChatLine ToLine(FriendMessage message) => new(
        string.IsNullOrWhiteSpace(message.SenderName) ? message.Sender : message.SenderName,
        message.Content, message.Timestamp.ToLocalTime().ToString("HH:mm"),
        string.Equals(message.Sender.Replace("-", ""), _session?.Uuid.Replace("-", ""), StringComparison.OrdinalIgnoreCase));

    private void ShowLines(ChatLine[] lines)
    {
        Messages.ItemsSource = lines;
        if (lines.Length > 0) Messages.ScrollIntoView(lines[^1]);
        UpdateChatState();
    }

    private sealed class ChatStore { public Dictionary<string, List<FriendMessage>> Channels { get; set; } = new(StringComparer.Ordinal); }

    private ChatStore LoadStore()
    {
        try
        {
            if (!File.Exists(_cachePath) || new FileInfo(_cachePath).Length > 4_000_000) return new ChatStore();
            return JsonSerializer.Deserialize<ChatStore>(File.ReadAllText(_cachePath)) ?? new ChatStore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new ChatStore(); }
    }

    private List<FriendMessage> ReadCache(string self, string channel)
    {
        var key = FriendApiClient.MojangUuid(self) + ":" + channel;
        return LoadStore().Channels.TryGetValue(key, out var messages) ? messages : [];
    }

    private void SaveCache(string self, string channel, IReadOnlyList<FriendMessage> messages)
    {
        try
        {
            var store = LoadStore();
            store.Channels[FriendApiClient.MojangUuid(self) + ":" + channel] = messages.TakeLast(200).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(store));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private List<ChatRoom> Dedupe(List<ChatRoom> rooms)
    {
        var grouped = rooms.Where(room => room.Direct && room.Peer is not null)
            .GroupBy(room => room.Peer!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(room => _session is not null && ReadCache(_session.Uuid, room.Id).Count > 0)
                .ThenByDescending(room => long.TryParse(room.Id, out var id) ? id : 0).First());
        return grouped.Concat(rooms.Where(room => !room.Direct || room.Peer is null)).ToList();
    }

    private void ToggleAdd_Click(object sender, RoutedEventArgs e)
    {
        Status.Text = _premium ? "输入好友的正版用户名或 UUID。" : "请先登录正版微软账号后添加好友。";
        OpenOverlay(AddPanel, "添加好友", Target);
    }

    private void OpenOverlay(FrameworkElement panelShown, string title, Control focus)
    {
        _overlayFocus = Keyboard.FocusedElement;
        foreach (var panel in new FrameworkElement[] { AddPanel, Settings, ActivityPanel })
            panel.Visibility = Show(ReferenceEquals(panel, panelShown));
        OverlayTitle.Text = title;
        Overlay.Visibility = Visibility.Visible;
        Overlay.UpdateLayout();
        if (!focus.Focus()) CloseOverlayButton.Focus();
    }

    private void CloseOverlay()
    {
        Overlay.Visibility = Visibility.Collapsed;
        if (_overlayFocus is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
        _overlayFocus = null;
    }

    private void CloseOverlay_Click(object sender, RoutedEventArgs e) => CloseOverlay();

    private void Friends_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Overlay.Visibility != Visibility.Visible) return;
        CloseOverlay();
        e.Handled = true;
    }

    private void Target_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        Request_Click(sender, e);
    }

    private void StartGateway(string url, string token, int generation)
    {
        var tokenSource = _lifetime;
        _ = Task.Run(async () =>
        {
            var delay = TimeSpan.FromSeconds(2);
            while (!tokenSource.IsCancellationRequested && generation == _generation)
            {
                try
                {
                    await using var gateway = new FriendGateway(url, token);
                    delay = TimeSpan.FromSeconds(2);
                    await gateway.ListenAsync(async (friendEvent, ct) =>
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            if (generation != _generation) return;
                            var notice = friendEvent.Target switch
                            {
                                "friend_request" => "收到新的好友申请。",
                                "friend_request_accept" => "对方接受了好友申请。",
                                "friend_request_deny" => "对方拒绝了好友申请。",
                                "chat_message" => (string.IsNullOrWhiteSpace(friendEvent.SenderName) ? "好友" : friendEvent.SenderName) + "：" + friendEvent.Content,
                                "channel_invite" => "收到频道邀请" + (string.IsNullOrWhiteSpace(friendEvent.ChannelName) ? "。" : "：" + friendEvent.ChannelName),
                                "channel_invite_reaction" => "频道邀请已有回应。",
                                _ => "好友活动已更新。"
                            };
                            if (_notices && friendEvent.Target != "chat_message") Status.Text = notice;

                            if (friendEvent.Target == "chat_message" && !string.Equals(friendEvent.From, _session?.Uuid, StringComparison.OrdinalIgnoreCase))
                            {
                                Announce(notice);
                                PromptRoomInvite(friendEvent.Content, friendEvent.MessageId);
                            }

                            if (friendEvent.Target is "chat_message" or "channel_invite" or "channel_invite_reaction")
                            {
                                await LoadChannelsAsync().ConfigureAwait(true);
                                if (friendEvent.Target == "chat_message" && friendEvent.Channel == _channelId) await LoadMessagesAsync().ConfigureAwait(true);
                            }
                            else await RefreshAsync().ConfigureAwait(true);
                        });
                    }, tokenSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { }
                try { await Task.Delay(delay, tokenSource.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (delay < TimeSpan.FromSeconds(30)) delay += delay;
            }
        });
    }

    private void StopGateway()
    {
        _generation++;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
    }

    private string ServiceUrl() => Service.SelectedIndex == 1 ? CustomUrl.Text.Trim() : FriendApiClient.OfficialUrl;

    private void ToggleSettings_Click(object sender, RoutedEventArgs e) => OpenOverlay(Settings, "服务与隐私", Service);

    private void Service_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        CustomUrl.Visibility = Service.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SaveSettings();
        ResetSession();
    }

    private void CustomUrl_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loaded) SaveSettings();
    }

    private void ResetSession()
    {
        StopGateway();
        _session = null;
        _api?.Dispose();
        _api = null;
        ResetView();
        if (_premium) _ = ConnectAsync();
    }

    private sealed record Preferences(bool UseCustom = false, string CustomUrl = "", bool Notices = true);

    private void LoadSettings()
    {
        try
        {
            var data = File.Exists(_settingsPath) && new FileInfo(_settingsPath).Length < 8192
                ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_settingsPath)) : null;
            var custom = Saved(data?.CustomUrl);
            CustomUrl.Text = custom;
            Service.SelectedIndex = data?.UseCustom == true && custom.Length > 0 ? 1 : 0;
            FriendNotices.IsChecked = _notices = data?.Notices ?? true;
            CustomUrl.Visibility = Service.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    private static string Saved(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || FriendApiClient.IsOfficial(value)) return "";
        try { return FriendApiClient.Normalize(value).AbsoluteUri; }
        catch (ArgumentException) { return ""; }
    }

    private void SaveSettings()
    {
        if (_closed) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var custom = Saved(CustomUrl.Text);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new Preferences(Service.SelectedIndex == 1, custom, _notices)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void Notice_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _notices = FriendNotices.IsChecked == true;
        SaveSettings();
    }

    private async Task LoadPrivacyAsync()
    {
        if (_api is null || _session is null || _privacyReady) return;
        try
        {
            var settings = await _api.SettingsAsync(_session.AccessToken, _lifetime.Token).ConfigureAwait(true);
            _settings = settings;
            _privacyReady = true;
            ShowRegistered.IsChecked = settings.ShowRegistered;
            RetainUsernames.IsChecked = settings.RetainUsernames;
            ShowLastOnline.IsChecked = settings.ShowLastOnline;
            ShowActivity.IsChecked = settings.ShowActivity;
            AllowImages.IsChecked = settings.AllowFriendsImageAccess;
        }
        catch (Exception ex) when (ex is FriendApiException or OperationCanceledException) { }
    }

    private async void Privacy_Changed(object sender, RoutedEventArgs e)
    {
        if (!_privacyReady || _api is null || _session is null) return;
        var next = new FriendSettings(ShowRegistered.IsChecked == true, RetainUsernames.IsChecked == true, ShowLastOnline.IsChecked == true, ShowActivity.IsChecked == true, AllowImages.IsChecked == true);
        if (next == _settings) return;
        _settings = next;
        try { await _api.UpdateSettingsAsync(_session.AccessToken, next, _lifetime.Token).ConfigureAwait(true); Status.Text = "隐私设置已保存。"; }
        catch (Exception ex) { Status.Text = Message(ex); }
    }

    private async Task PublishActivityAsync()
    {
        if (_api is null || _session is null || _settings?.ShowActivity == false) return;
        try { await _api.UpdateActivityAsync(_session.AccessToken, "在启动器", "MOONCL", _lifetime.Token).ConfigureAwait(true); }
        catch (Exception ex) when (ex is FriendApiException or OperationCanceledException) { }
    }

    private static string Message(Exception ex) => ex switch
    {
        OperationCanceledException => "操作已取消。",
        FriendApiException or ArgumentException => ex.Message,
        _ => "好友操作失败，请检查网络后重试。"
    };

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        SaveSettings();
        StopGateway();
        _api?.Dispose();
        _gate.Dispose();
    }

    public void BeginRoomInvite(string code, string? roomName)
    {
        var room = (code ?? "").Trim().ToUpperInvariant();
        if (room.Length != 8 || !room.All(char.IsAsciiLetterOrDigit)) { Status.Text = "房间邀请码无效。"; return; }
        _pendingRoomCode = room;
        _pendingRoomName = string.IsNullOrWhiteSpace(roomName) ? room : roomName.Trim();
        CloseOverlay();
        _tab = "all";
        Search.Clear();
        ShowTab();
        Status.Text = "选择一位好友，点「邀请进房」。邀请会发到私聊。";
        UpdateActions();
    }

    private async void InviteRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingRoomCode is null) { Status.Text = "请先在联机房间里发起邀请。"; return; }
        if (People.SelectedItem is not Person { Kind: "friend" } person || !await EnsureSessionAsync().ConfigureAwait(true) || _api is null || _session is null) return;
        var selection = _selectionVersion;
        try
        {
            var channel = await _api.GetOrCreateDirectAsync(_session.AccessToken, _session.Uuid, person.Uuid, _lifetime.Token).ConfigureAwait(true);
            var text = "邀请你加入联机房间「" + _pendingRoomName + "」，邀请码 " + _pendingRoomCode + "。";
            await _api.SendMessageAsync(_session.AccessToken, channel, text, _session.Username, _lifetime.Token).ConfigureAwait(true);
            if (selection != _selectionVersion) return;
            _channelId = channel;
            await LoadChannelsAsync().ConfigureAwait(true);
            if (selection != _selectionVersion) return;
            await LoadMessagesAsync().ConfigureAwait(true);
            Status.Text = "已把房间 " + _pendingRoomCode + " 发给 " + person.Name + "。";
        }
        catch (Exception ex) { Status.Text = Message(ex); }
    }

    private void PromptRoomInvite(string? text, string? messageId)
    {
        if (_closed) return;
        var code = RoomFlowPresentation.RoomInviteCode(text);
        if (code is null) return;
        var key = string.IsNullOrWhiteSpace(messageId) ? code : messageId;
        _promptedInviteKey = key;

        _pendingJoinCode = code;
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exe)) { PCL.Core.UI.ToastNotification.SendToast("邀请码 " + code, "好友邀请你加入房间"); return; }
            PCL.Core.UI.ToastNotification.SendActionToast("邀请码 " + code, "好友邀请你加入房间", "加入", exe + " --join-room " + code);
        }
        catch (Exception) { }

    }



    private static void Announce(string text)
    {
        var title = "MOONCL";
        var body = text;
        var split = text.IndexOf('：');
        if (split > 0 && split < 24) { title = text[..split]; body = text[(split + 1)..]; }
        try { PCL.Core.UI.ToastNotification.SendToast(body, title); }
        catch (Exception) { }
    }

}
