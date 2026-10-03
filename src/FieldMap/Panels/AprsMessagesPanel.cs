using FieldMap.Core.Aprs;
using FieldMap.Views;
using Shared;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>
/// APRS messages: conversations by station (newest first, unread marked), one conversation with delivery status, a
/// compose box with quick phrases for gloves, and bulletins.
/// </summary>
public sealed class AprsMessagesPanel : SidePanel
{
    private static readonly string[] QuickPhrases = { "Copy", "On scene", "En route", "Available", "Returning", "Need assistance", "Call me" };

    private readonly TextBox _to = new() { CharacterCasing = CharacterCasing.Upper, MaxLength = 9 };
    private readonly TextBox _text = new() { MaxLength = AprsMessenger.MaxText };
    private string? _peer;
    private int _shownVersion = -1;

    public AprsMessagesPanel(MapWindow map, string? peer = null) : base(map, "APRS messages")
    {
        _peer = peer;
        _to.Styled(FontRole.Large);
        _text.Styled(FontRole.Large);
        _text.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Send();
            }
        };
        if (peer != null)
            _to.Text = peer;
        Build();
        AppServices.ClockTick += OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (AppServices.Aprs.Messenger.Version != _shownVersion && !_text.Focused && !_to.Focused)
            Build();
    }

    private void Build()
    {
        var messenger = AppServices.Aprs.Messenger;
        _shownVersion = messenger.Version;
        if (_peer != null)
            messenger.MarkRead(_peer);
        var all = messenger.Snapshot();
        var keepTo = _to.Text;
        var keepText = _text.Text;
        Stack.SuspendLayout();
        // Keep the compose boxes (with what's typed) across refreshes.
        Stack.Controls.Remove(_to);
        Stack.Controls.Remove(_text);
        Clear();
        Title = _peer != null ? $"Messages · {_peer}" : "APRS messages";

        var aprs = AppServices.Aprs;
        var s = aprs.Settings;
        if (!AprsBeacon.IsValidCallsign(s.Callsign))
            AddText("Set your call sign in APRS settings to send and receive messages. Messages are amateur radio transmissions.", FontRole.Bold, Brand.Danger);
        else if (!aprs.CanSendMessages)
            AddText("Can't send right now: no TNC open, and APRS-IS isn't logged in with a passcode. Received messages still show here.", FontRole.Bold, Brand.Danger);
        else if (!s.MessagingEnabled)
            AddText("Acknowledgements are off in APRS settings: senders will keep retrying.", FontRole.Bold, Brand.Danger);

        // Compose
        AddText("To (call sign)", FontRole.Bold);
        _to.Text = keepTo;
        _to.Width = Math.Max(160, Stack.ClientSize.Width - 30);
        Stack.Controls.Add(_to);
        AddText($"Message (up to {AprsMessenger.MaxText} characters)", FontRole.Bold);
        _text.Text = keepText;
        _text.Width = _to.Width;
        Stack.Controls.Add(_text);
        var quick = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 2), MaximumSize = new Size(_to.Width + 10, 0) };
        foreach (var phrase in QuickPhrases)
        {
            var text = phrase;
            var b = Ui.Button(text, ButtonStyle.Secondary, () =>
            {
                _text.Text = (_text.Text.Length > 0 ? _text.Text.TrimEnd() + " " : "") + text;
                _text.Focus();
                _text.SelectionStart = _text.TextLength;
            }, FontRole.Normal);
            b.AutoSize = true;
            b.Margin = new Padding(0, 0, 4, 4);
            quick.Controls.Add(b);
        }
        Stack.Controls.Add(quick);
        AddButtons(Btn("Send", ButtonStyle.Accent, Send),
                   _peer != null ? Btn("All conversations", ButtonStyle.Secondary, () => { _peer = null; Build(); })
                                 : Btn("APRS stations", ButtonStyle.Secondary, () => Map.ShowPanel(new AprsPanel(Map))));

        if (_peer != null)
            BuildConversation(all.Where(m => m.State != AprsMessageState.Bulletin && m.Peer.Equals(_peer, StringComparison.OrdinalIgnoreCase)).ToList());
        else
            BuildList(all);
        Stack.ResumeLayout();
        Refit();
    }

    private void BuildList(List<AprsMessage> all)
    {
        var conversations = all.Where(m => m.State != AprsMessageState.Bulletin)
            .GroupBy(m => m.Peer, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Peer: g.Key, Last: g.MaxBy(m => m.TimeUtc)!, Unread: g.Count(m => !m.Outgoing && !m.Read)))
            .OrderByDescending(c => c.Last.TimeUtc).ToList();
        AddText("Conversations", FontRole.Heading);
        if (conversations.Count == 0)
            AddText("No messages yet.", FontRole.Normal, Brand.Muted);
        foreach (var c in conversations)
        {
            var peer = c.Peer;
            var title = (c.Unread > 0 ? $"✉ {c.Unread} new · " : "") + peer;
            var detail = $"{c.Last.TimeUtc.ToLocalTime():HH:mm} {(c.Last.Outgoing ? "→ " : "← ")}{c.Last.Text}";
            AddRow(title, detail, () => { _peer = peer; _to.Text = peer; Build(); }, c.Unread > 0 ? Brand.Amber : Brand.Navy);
        }
        var bulletins = all.Where(m => m.State == AprsMessageState.Bulletin).OrderByDescending(m => m.TimeUtc).Take(30).ToList();
        if (bulletins.Count > 0)
        {
            AddText("Bulletins heard", FontRole.Heading);
            foreach (var b in bulletins)
                AddText($"{b.TimeUtc.ToLocalTime():HH:mm} {b.From} ({b.To}): {b.Text}");
        }
    }

    private void BuildConversation(List<AprsMessage> messages)
    {
        AddText("Conversation (newest first)", FontRole.Heading);
        if (messages.Count == 0)
            AddText("No messages with this station yet.", FontRole.Normal, Brand.Muted);
        foreach (var m in messages.OrderByDescending(m => m.TimeUtc).Take(100))
        {
            var msg = m;
            var head = $"{m.TimeUtc.ToLocalTime():HH:mm:ss} {(m.Outgoing ? "→ me to " + m.To : "← " + m.From)}";
            var status = m.Outgoing ? m.StateText : m.Via == AprsVia.Radio ? "by radio" : m.Via == AprsVia.Internet ? "by internet" : "by radio and internet";
            var color = m.State is AprsMessageState.Failed or AprsMessageState.Rejected ? Brand.Danger : m.Outgoing ? Brand.Navy : Brand.Success;
            if (m.Outgoing && m.State == AprsMessageState.Failed)
                AddRow($"{m.Text}", $"{head} · {status} · tap to send again", () => { AppServices.Aprs.Messenger.Retry(msg.Id, DateTime.UtcNow); Build(); }, color);
            else
                AddRow(m.Text, $"{head}{(status.Length > 0 ? " · " + status : "")}", () => Map.CopyText(msg.Text, "Copied message"), color);
        }
        if (messages.Count > 0)
            AddButton("Delete this conversation", ButtonStyle.Secondary, () =>
            {
                if (MessageBox.Show(FindForm(), $"Delete the messages with {_peer}?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                AppServices.Aprs.Messenger.ClearConversation(_peer!);
                _peer = null;
                Build();
            });
    }

    private void Send()
    {
        var to = _to.Text.Trim().ToUpperInvariant();
        if (!AprsMessenger.IsValidAddressee(to))
        {
            MessageBox.Show(FindForm(), "Enter the call sign to send to (e.g. VE7ABC-9).", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_text.Text))
            return;
        try
        {
            AppServices.Aprs.Messenger.Send(to, _text.Text, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(FindForm(), ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _text.Text = "";
        _peer = to;
        Build();
    }

    public override void OnClosing() => AppServices.ClockTick -= OnTick;
}
