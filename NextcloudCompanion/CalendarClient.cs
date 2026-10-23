using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NextcloudCompanion;

public sealed class CloudCalendar
{
    public required string Name { get; init; }
    public required string Href { get; init; }
    public override string ToString() => Name;
}

public sealed class PersonHit
{
    public required string Name { get; init; }
    public string? Email { get; set; }
    public string? UserId { get; init; }
    public string? GroupId { get; init; }
    public bool IsGroup { get; init; }

    public string Kind => IsGroup ? "Group" : "Person";

    public override string ToString()
    {
        if (IsGroup)
            return string.IsNullOrEmpty(GroupId) ? Name : $"{Name}  ({GroupId})";
        return string.IsNullOrEmpty(Email) ? Name : $"{Name}  <{Email}>";
    }
}

public sealed class Invitee
{
    public required string Name { get; init; }
    public required string Email { get; init; }
    public bool IsGroup { get; init; }
    /// <summary>Set on a person who was added because they belong to this group mailto.</summary>
    public string? MemberOfGroupEmail { get; init; }

    public override string ToString()
        => IsGroup ? $"Group  {Name}" : $"{Name}  <{Email}>";
}

public sealed class MeetingDraft
{
    public required string Title { get; init; }
    public string Location { get; init; } = "";
    public string Description { get; init; } = "";
    public required DateTime StartLocal { get; init; }
    public required DateTime EndLocal { get; init; }
    public required string OrganizerName { get; init; }
    public required string OrganizerEmail { get; init; }
    public required IReadOnlyList<Invitee> Invitees { get; init; }
}

public sealed class NextcloudApiException : Exception
{
    public int StatusCode { get; }

    public NextcloudApiException(string action, int statusCode, string body)
        : base(Format(action, statusCode, body))
    {
        StatusCode = statusCode;
    }

    private static string Format(string action, int statusCode, string body)
    {
        string detail = Brief(body);
        string why = statusCode switch
        {
            401 => "The app password was rejected.",
            403 => "This account is not allowed to do that.",
            404 => "The server has no such resource.",
            _ => ""
        };
        if (why.Length == 0 && detail.Length == 0)
            return $"{action} failed ({statusCode}).";
        if (detail.Length == 0)
            return $"{action} failed ({statusCode}). {why}";
        if (why.Length == 0)
            return $"{action} failed ({statusCode}). {detail}";
        return $"{action} failed ({statusCode}). {why} {detail}";
    }

    private static string Brief(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("ocs", out var ocs)
                && ocs.TryGetProperty("meta", out var meta)
                && meta.TryGetProperty("message", out var message))
            {
                string? text = message.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
        }
        catch { /* not JSON */ }

        string plain = Regex.Replace(body, "<[^>]+>", " ");
        plain = Regex.Replace(plain, "\\s+", " ").Trim();
        return plain.Length <= 240 ? plain : plain[..240];
    }
}

/// <summary>
/// CalDAV plus the sharing search. The calendar app's own attendee autocomplete is the
/// broken endpoint, so people and groups are resolved through sharees, the system
/// address book, and group principals instead.
/// </summary>
public sealed class CalendarClient : IDisposable
{
    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Report = new("REPORT");

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _user;
    private readonly string _host;
    private readonly Dictionary<string, (string Name, string? Email)> _principals = new(StringComparer.OrdinalIgnoreCase);

    public CalendarClient(string serverUrl, string davUser, string appPassword)
    {
        _base = serverUrl.TrimEnd('/');
        _user = davUser;
        _host = Uri.TryCreate(_base, UriKind.Absolute, out var uri) ? uri.Host : serverUrl;
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(davUser + ":" + appPassword));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NextcloudCompanion/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/xml");
    }

    public async Task<(string Name, string Email)> GetMeAsync(CancellationToken ct)
    {
        var (name, email) = await PrincipalAsync(_user, ct);
        if (string.IsNullOrWhiteSpace(email))
            email = _user.Contains('@') ? _user : _user + "@" + _host;
        if (string.IsNullOrWhiteSpace(name))
            name = _user;
        return (name, email);
    }

    public async Task<IReadOnlyList<CloudCalendar>> ListCalendarsAsync(CancellationToken ct)
    {
        string home = "/remote.php/dav/calendars/" + Uri.EscapeDataString(_user) + "/";
        XDocument doc = await DavAsync(Propfind, home, "1", """
            <?xml version="1.0"?>
            <d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop>
                <d:displayname />
                <d:resourcetype />
                <d:current-user-privilege-set />
                <c:supported-calendar-component-set />
              </d:prop>
            </d:propfind>
            """, ct);

        var calendars = new List<CloudCalendar>();
        foreach (var (href, prop) in Responses(doc))
        {
            string leaf = Leaf(href);
            if (leaf.Length == 0 || leaf.Equals(_user, StringComparison.OrdinalIgnoreCase))
                continue;
            if (leaf is "inbox" or "outbox" or "trashbin")
                continue;
            if (!prop.Descendants().Any(e => e.Name.LocalName == "calendar"))
                continue;

            var components = prop.Descendants()
                .Where(e => e.Name.LocalName == "comp")
                .Select(e => (string?)e.Attribute("name") ?? "")
                .Where(n => n.Length > 0)
                .ToList();
            if (components.Count > 0 && !components.Any(n => n.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)))
                continue;

            bool canWrite = prop.Descendants().Any(e =>
                e.Parent?.Name.LocalName == "privilege"
                && e.Name.LocalName is "write" or "write-content" or "all");
            if (!canWrite)
                continue;

            string name = Text(prop, "displayname");
            if (string.IsNullOrWhiteSpace(name))
                name = Uri.UnescapeDataString(leaf);
            calendars.Add(new CloudCalendar { Name = name, Href = href });
        }

        return calendars.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<PersonHit>> SearchPeopleAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        var hits = new List<PersonHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Exception? shareError = null;
        Exception? bookError = null;

        try { await AddShareesAsync(query, hits, seen, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { shareError = ex; }

        try { await AddAddressBookAsync(query, hits, seen, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { bookError = ex; }

        var missing = hits.Where(h => !h.IsGroup && string.IsNullOrWhiteSpace(h.Email) && !string.IsNullOrWhiteSpace(h.UserId)).Take(20).ToList();
        foreach (var hit in missing)
        {
            ct.ThrowIfCancellationRequested();
            var (_, email) = await PrincipalAsync(hit.UserId!, ct);
            if (!string.IsNullOrWhiteSpace(email))
                hit.Email = email;
        }

        if (hits.Count == 0 && (shareError ?? bookError) is Exception failure)
            throw failure;

        return hits
            .GroupBy(h => h.IsGroup ? "g:" + h.GroupId : "e:" + (h.Email ?? h.UserId ?? h.Name), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(h => h.IsGroup ? 1 : 0)
            .ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task<(string Name, string? Email)> LookupUserAsync(string userId, CancellationToken ct)
        => PrincipalAsync(userId, ct);

    public async Task<IReadOnlyList<Invitee>> ExpandGroupAsync(PersonHit group, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(group.GroupId))
            throw new InvalidOperationException("That group has no id.");

        string groupMail = GroupMail(group.GroupId);
        var members = await GroupMemberIdsAsync(group.GroupId, ct);
        var invitees = new List<Invitee>
        {
            new() { Name = group.Name, Email = groupMail, IsGroup = true }
        };

        var gate = new SemaphoreSlim(6);
        var tasks = members.Take(200).Select(async id =>
        {
            await gate.WaitAsync(ct);
            try { return await PrincipalAsync(id, ct); }
            finally { gate.Release(); }
        });
        foreach (var (name, email) in await Task.WhenAll(tasks))
        {
            if (string.IsNullOrWhiteSpace(email))
                continue;
            invitees.Add(new Invitee
            {
                Name = string.IsNullOrWhiteSpace(name) ? email : name,
                Email = email,
                MemberOfGroupEmail = groupMail
            });
        }

        return invitees;
    }

    public async Task CreateMeetingAsync(CloudCalendar calendar, MeetingDraft draft, CancellationToken ct)
    {
        string uid = Guid.NewGuid().ToString("N") + "@" + _host;
        string href = calendar.Href.TrimEnd('/') + "/" + uid + ".ics";
        string ics = BuildIcs(uid, draft);
        using var req = new HttpRequestMessage(HttpMethod.Put, Absolute(href));
        req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        req.Content = new StringContent(ics, Encoding.UTF8, "text/calendar");
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new NextcloudApiException("Creating the meeting", (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    public void Dispose() => _http.Dispose();

    private string GroupMail(string groupId) => groupId + "@" + _host;

    private async Task<List<string>> GroupMemberIdsAsync(string groupId, CancellationToken ct)
    {
        // The DAV group principal answers PROPFIND with HTTP 500 TypeError on some
        // servers (seen for some groups). The provisioning API is the reliable list.
        string escaped = Uri.EscapeDataString(groupId);
        string[] urls =
        [
            _base + "/ocs/v2.php/cloud/groups/" + escaped + "?format=json",
            _base + "/ocs/v1.php/cloud/groups/" + escaped + "?format=json",
            _base + "/ocs/v2.php/cloud/groups/" + escaped + "/users?format=json",
            _base + "/ocs/v2.php/apps/provisioning_api/api/v1/groups/" + escaped + "/users?format=json"
        ];
        foreach (string url in urls)
        {
            var ids = await TryOcsUserIds(url, ct);
            if (ids is { Count: > 0 })
                return ids;
        }

        try
        {
            string href = "/remote.php/dav/principals/groups/" + escaped + "/";
            XDocument memberDoc = await DavAsync(Propfind, href, "0", """
                <?xml version="1.0"?>
                <d:propfind xmlns:d="DAV:">
                  <d:prop><d:group-member-set /></d:prop>
                </d:propfind>
                """, ct);
            return memberDoc.Descendants()
                .Where(e => e.Name.LocalName == "href" && e.Parent?.Name.LocalName == "group-member-set")
                .Select(e => Leaf(e.Value))
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (NextcloudApiException ex)
        {
            ErrorLog.Write("Group member lookup failed for " + groupId, ex);
            return [];
        }
    }

    /// <summary>Null when this URL is not a usable member list. Empty when the group has no users.</summary>
    private async Task<List<string>?> TryOcsUserIds(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("application/json");
            using var res = await _http.SendAsync(req, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
                return null;
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("ocs", out var ocs))
                return null;
            if (ocs.TryGetProperty("meta", out var meta)
                && meta.TryGetProperty("status", out var status)
                && status.GetString() is "failure" or "error")
                return null;
            if (!ocs.TryGetProperty("data", out var data))
                return null;
            var ids = ReadUserIds(data);
            return ids;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorLog.Write("Group member request failed", ex);
            return null;
        }
    }

    private static List<string> ReadUserIds(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
            return UserIdsFromArray(data);
        if (data.ValueKind != JsonValueKind.Object)
            return [];
        if (data.TryGetProperty("users", out var users))
        {
            if (users.ValueKind == JsonValueKind.Array)
                return UserIdsFromArray(users);
            if (users.ValueKind == JsonValueKind.Object)
                return users.EnumerateObject().Select(p => p.Name).Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return [];
    }

    private static List<string> UserIdsFromArray(JsonElement array)
    {
        var ids = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? id = item.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                string? id = item.TryGetProperty("id", out var idProp) ? idProp.GetString()
                    : item.TryGetProperty("userid", out var userProp) ? userProp.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task AddShareesAsync(string query, List<PersonHit> hits, HashSet<string> seen, CancellationToken ct)
    {
        string url = _base
            + "/ocs/v2.php/apps/files_sharing/api/v1/sharees?format=json&itemType=file&page=1&perPage=30&search="
            + Uri.EscapeDataString(query);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await _http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new NextcloudApiException("Searching people", (int)res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("ocs", out var ocs) || !ocs.TryGetProperty("data", out var data))
            return;

        void Take(JsonElement array, bool group)
        {
            if (array.ValueKind != JsonValueKind.Array)
                return;
            foreach (var item in array.EnumerateArray())
            {
                string label = item.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                string id = "";
                if (item.TryGetProperty("value", out var value) && value.TryGetProperty("shareWith", out var shareWith))
                    id = shareWith.GetString() ?? "";
                if (id.Length == 0)
                    continue;
                string key = (group ? "g:" : "u:") + id;
                if (!seen.Add(key))
                    continue;
                hits.Add(new PersonHit
                {
                    Name = string.IsNullOrWhiteSpace(label) ? id : label,
                    UserId = group ? null : id,
                    GroupId = group ? id : null,
                    IsGroup = group
                });
            }
        }

        void TakeBucket(JsonElement bucket)
        {
            if (bucket.ValueKind != JsonValueKind.Object)
                return;
            if (bucket.TryGetProperty("users", out var users))
                Take(users, group: false);
            if (bucket.TryGetProperty("groups", out var groups))
                Take(groups, group: true);
        }

        TakeBucket(data);
        if (data.TryGetProperty("exact", out var exact))
            TakeBucket(exact);
    }

    private async Task AddAddressBookAsync(string query, List<PersonHit> hits, HashSet<string> seen, CancellationToken ct)
    {
        string home = "/remote.php/dav/addressbooks/users/" + Uri.EscapeDataString(_user) + "/";
        XDocument listed;
        try
        {
            listed = await DavAsync(Propfind, home, "1", """
                <?xml version="1.0"?>
                <d:propfind xmlns:d="DAV:">
                  <d:prop><d:displayname /><d:resourcetype /></d:prop>
                </d:propfind>
                """, ct);
        }
        catch (NextcloudApiException)
        {
            return;
        }

        var books = Responses(listed)
            .Where(r => r.Prop.Descendants().Any(e => e.Name.LocalName == "addressbook"))
            .Select(r => r.Href)
            .OrderByDescending(href => href.Contains("system", StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToList();

        string escaped = Xml(query);
        string report = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <C:addressbook-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
              <D:prop><C:address-data /></D:prop>
              <C:filter test="anyof">
                <C:prop-filter name="FN">
                  <C:text-match collation="i;unicode-casemap" match-type="contains">{escaped}</C:text-match>
                </C:prop-filter>
                <C:prop-filter name="EMAIL">
                  <C:text-match collation="i;unicode-casemap" match-type="contains">{escaped}</C:text-match>
                </C:prop-filter>
              </C:filter>
            </C:addressbook-query>
            """;

        foreach (string book in books)
        {
            ct.ThrowIfCancellationRequested();
            XDocument cards;
            try { cards = await DavAsync(Report, book, "1", report, ct); }
            catch (NextcloudApiException) { continue; }

            foreach (var card in cards.Descendants().Where(e => e.Name.LocalName == "address-data"))
            {
                var parsed = ParseCard(card.Value);
                if (parsed.Email is null)
                    continue;
                if (!parsed.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && !parsed.Email.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && !(parsed.UserId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    continue;
                string key = "e:" + parsed.Email;
                if (!seen.Add(key))
                    continue;
                hits.Add(parsed);
                if (hits.Count >= 40)
                    return;
            }
        }
    }

    private static PersonHit ParseCard(string vcard)
    {
        string unfolded = vcard.Replace("\r\n ", "").Replace("\n ", "").Replace("\r\n\t", "").Replace("\n\t", "");
        string name = "";
        string? email = null;
        string? uid = null;
        foreach (var raw in unfolded.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon];
            string value = line[(colon + 1)..].Trim();
            string prop = key.Split(';')[0];
            if (prop.Equals("FN", StringComparison.OrdinalIgnoreCase) && name.Length == 0)
                name = value;
            else if (prop.Equals("EMAIL", StringComparison.OrdinalIgnoreCase) && value.Contains('@') && email is null)
                email = value;
            else if (prop.Equals("UID", StringComparison.OrdinalIgnoreCase) && uid is null)
                uid = value;
        }
        if (name.Length == 0)
            name = email ?? uid ?? "";
        return new PersonHit { Name = name, Email = email, UserId = uid, IsGroup = false };
    }

    private async Task<(string Name, string? Email)> PrincipalAsync(string userId, CancellationToken ct)
    {
        if (_principals.TryGetValue(userId, out var cached))
            return cached;

        string href = "/remote.php/dav/principals/users/" + Uri.EscapeDataString(userId) + "/";
        try
        {
            XDocument doc = await DavAsync(Propfind, href, "0", """
                <?xml version="1.0"?>
                <d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:s="http://sabredav.org/ns">
                  <d:prop>
                    <d:displayname />
                    <s:email-address />
                    <c:calendar-user-address-set />
                  </d:prop>
                </d:propfind>
                """, ct);
            var prop = Responses(doc).Select(r => r.Prop).FirstOrDefault();
            string name = prop is null ? userId : Text(prop, "displayname");
            string? email = prop?.Descendants()
                .Where(e => e.Name.LocalName == "href")
                .Select(e => Uri.UnescapeDataString(e.Value.Trim()))
                .FirstOrDefault(v => v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && v.Contains('@'));
            if (email is not null)
                email = email["mailto:".Length..];
            if (string.IsNullOrWhiteSpace(email) && prop is not null)
            {
                string direct = Text(prop, "email-address");
                if (direct.Contains('@'))
                    email = direct;
            }
            if (string.IsNullOrWhiteSpace(name))
                name = userId;
            var result = (name, email);
            _principals[userId] = result;
            return result;
        }
        catch (NextcloudApiException)
        {
            var missing = (userId, (string?)null);
            _principals[userId] = missing;
            return missing;
        }
    }

    private async Task<XDocument> DavAsync(HttpMethod method, string href, string depth, string xml, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, Absolute(href));
        req.Headers.Add("Depth", depth);
        req.Content = new StringContent(xml, Encoding.UTF8, "application/xml");
        using var res = await _http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new NextcloudApiException(method.Method + " " + Leaf(href), (int)res.StatusCode, body);
        return XDocument.Parse(body);
    }

    private Uri Absolute(string href)
    {
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new Uri(href);
        if (!href.StartsWith('/'))
            href = "/" + href;
        return new Uri(_base + href);
    }

    private static List<(string Href, XElement Prop)> Responses(XDocument doc)
    {
        var list = new List<(string, XElement)>();
        foreach (var resp in doc.Descendants().Where(e => e.Name.LocalName == "response"))
        {
            string href = resp.Elements().FirstOrDefault(e => e.Name.LocalName == "href")?.Value?.Trim() ?? "";
            var prop = resp.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "propstat"
                && e.Descendants().Any(s => s.Name.LocalName == "status" && s.Value.Contains("200", StringComparison.Ordinal)))
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "prop");
            prop ??= resp.Descendants().FirstOrDefault(e => e.Name.LocalName == "prop");
            if (href.Length > 0 && prop is not null)
                list.Add((href, prop));
        }
        return list;
    }

    private static string Text(XElement prop, string local)
        => prop.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value.Trim() ?? "";

    private static string Leaf(string href)
    {
        string trimmed = Uri.UnescapeDataString(href.Trim()).TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    private static string Xml(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string BuildIcs(string uid, MeetingDraft draft)
    {
        var sb = new StringBuilder();
        void Line(string line)
        {
            int take = 73;
            while (line.Length > take)
            {
                sb.Append(line[..take]).Append("\r\n");
                line = " " + line[take..];
                take = 74;
            }
            sb.Append(line).Append("\r\n");
        }

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//Nextcloud Companion//EN");
        Line("CALSCALE:GREGORIAN");
        Line("BEGIN:VEVENT");
        Line("UID:" + uid);
        Line("DTSTAMP:" + Stamp(DateTime.UtcNow));
        Line("DTSTART:" + Stamp(AsUtc(draft.StartLocal)));
        Line("DTEND:" + Stamp(AsUtc(draft.EndLocal)));
        Line("SUMMARY:" + IcalText(draft.Title));
        if (!string.IsNullOrWhiteSpace(draft.Location))
            Line("LOCATION:" + IcalText(draft.Location));
        if (!string.IsNullOrWhiteSpace(draft.Description))
            Line("DESCRIPTION:" + IcalText(draft.Description));
        Line("STATUS:CONFIRMED");
        Line("ORGANIZER;CN=" + Param(draft.OrganizerName) + ":mailto:" + draft.OrganizerEmail);
        foreach (var person in draft.Invitees)
        {
            if (person.Email.Equals(draft.OrganizerEmail, StringComparison.OrdinalIgnoreCase))
                continue;
            string parms = "CN=" + Param(person.Name)
                + ";CUTYPE=" + (person.IsGroup ? "GROUP" : "INDIVIDUAL")
                + ";ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE";
            if (!string.IsNullOrWhiteSpace(person.MemberOfGroupEmail))
                parms += ";MEMBER=\"mailto:" + person.MemberOfGroupEmail + "\"";
            Line("ATTENDEE;" + parms + ":mailto:" + person.Email);
        }
        Line("END:VEVENT");
        Line("END:VCALENDAR");
        return sb.ToString();
    }

    private static DateTime AsUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc)
            return value;
        if (value.Kind == DateTimeKind.Local)
            return value.ToUniversalTime();
        return DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
    }

    private static string Stamp(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string IcalText(string value)
        => value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");

    private static string Param(string value)
    {
        string escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return escaped.Any(c => c is ';' or ',' or ':' or ' ' or '\n') ? "\"" + escaped + "\"" : escaped;
    }
}
