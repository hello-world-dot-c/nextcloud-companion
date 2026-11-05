using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Net.Http.Headers;

namespace NextcloudCompanion;

/// <summary>Outcome of the server part of a file search.</summary>
public sealed class ServerSearchResult
{
    /// <summary>Items found on the server that the sync journal did not already list.</summary>
    public List<FileHit> Hits { get; } = [];
    /// <summary>Accounts that were not searched because no app password is saved for them.</summary>
    public List<string> NoPassword { get; } = [];
    /// <summary>Accounts whose search failed, as "account: reason".</summary>
    public List<string> Failed { get; } = [];
    /// <summary>True when an account returned as many results as were asked for (there may be more).</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Searches the whole server for files and folders by (part of) their name, including everything that is not in
/// a synced folder. Uses the WebDAV SEARCH method of Nextcloud over the account's home
/// (<c>/remote.php/dav</c>, scope <c>/files/USER</c>), authenticated with the app password saved for the account in
/// the meeting dialog (the desktop client's own login token is not reused). Accounts without a saved app password
/// are skipped and reported, never prompted for.
/// </summary>
public static class ServerFileSearch
{
    public const int PerAccountLimit = 200;

    private static readonly HttpMethod SearchMethod = new("SEARCH");

    /// <summary>Where the app password of an account comes from. Replaceable for tests; production uses the settings store.</summary>
    internal static Func<string, string, string?> PasswordFor { get; set; } = Settings.GetAppPassword;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NextcloudCompanion/1.0");
        return client;
    }

    /// <param name="known">Keys (<see cref="Key"/>) of hits already found locally; they are not returned again.</param>
    /// <param name="passwordFor">Looks up the saved app password for (server URL, user); defaults to <see cref="PasswordFor"/>.</param>
    public static async Task<ServerSearchResult> SearchAsync(
        IEnumerable<FolderStatus> folders,
        string query,
        ISet<string> known,
        Func<string, string, string?>? passwordFor = null,
        CancellationToken cancellationToken = default)
    {
        passwordFor ??= PasswordFor;
        var result = new ServerSearchResult();
        query = query.Trim();
        if (query.Length == 0)
            return result;

        var accounts = folders
            .Where(f => !string.IsNullOrWhiteSpace(f.ServerUrl) && !string.IsNullOrWhiteSpace(f.DavUser))
            .GroupBy(f => Settings.AccountKey(f.ServerUrl, f.DavUser))
            .Select(g => g.First())
            .ToList();

        var tasks = new List<Task<(FolderStatus Account, List<FileHit>? Hits, string? Error)>>();
        foreach (var account in accounts)
        {
            string? password = passwordFor(account.ServerUrl, account.DavUser);
            if (string.IsNullOrEmpty(password))
            {
                result.NoPassword.Add(account.Account);
                continue;
            }
            tasks.Add(SearchAccountAsync(account, password, query, cancellationToken));
        }

        foreach (var (account, hits, error) in await Task.WhenAll(tasks))
        {
            if (error is not null)
            {
                result.Failed.Add($"{account.Account}: {error}");
                continue;
            }
            if (hits!.Count >= PerAccountLimit)
                result.Truncated = true;
            foreach (var hit in hits)
                if (known.Add(Key(hit)))
                    result.Hits.Add(hit);
        }

        result.Hits.Sort((a, b) =>
        {
            int dir = (a.IsDirectory ? 0 : 1).CompareTo(b.IsDirectory ? 0 : 1);
            return dir != 0 ? dir : string.Compare(a.InternalPath, b.InternalPath, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    /// <summary>Identity of a hit for de-duplication between the journal and the server.</summary>
    public static string Key(FileHit hit) => hit.Account + "\n" + hit.InternalPath.TrimEnd('/');

    private static async Task<(FolderStatus, List<FileHit>?, string?)> SearchAccountAsync(
        FolderStatus account, string password, string query, CancellationToken ct)
    {
        try
        {
            string baseUrl = account.ServerUrl.TrimEnd('/');
            using var request = new HttpRequestMessage(SearchMethod, baseUrl + "/remote.php/dav/")
            {
                Content = new StringContent(BuildRequest(account.DavUser, query), Encoding.UTF8, "text/xml")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(account.DavUser + ":" + password)));

            using var response = await Http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                string reason = (int)response.StatusCode switch
                {
                    401 => "the app password was rejected",
                    405 or 501 => "the server does not support searching",
                    403 => "not allowed",
                    var code => "HTTP " + code
                };
                return (account, null, reason);
            }
            return (account, Parse(account, query, body), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return (account, null, "the server did not answer in time");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Server file search failed for " + account.Account, ex);
            return (account, null, ex.Message);
        }
    }

    internal static string BuildRequest(string user, string query)
    {
        // The home of the user. '@' stays literal: it is valid in a path and some servers do not decode %40 here.
        string home = "/files/" + Uri.EscapeDataString(user).Replace("%40", "@");
        string literal = "%" + query + "%";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <d:searchrequest xmlns:d="DAV:">
              <d:basicsearch>
                <d:select>
                  <d:prop><d:displayname/><d:getlastmodified/><d:resourcetype/></d:prop>
                </d:select>
                <d:from>
                  <d:scope><d:href>{Escape(home)}</d:href><d:depth>infinity</d:depth></d:scope>
                </d:from>
                <d:where>
                  <d:like><d:prop><d:displayname/></d:prop><d:literal>{Escape(literal)}</d:literal></d:like>
                </d:where>
                <d:orderby/>
                <d:limit><d:nresults>{PerAccountLimit}</d:nresults></d:limit>
              </d:basicsearch>
            </d:searchrequest>
            """;
    }

    internal static List<FileHit> Parse(FolderStatus account, string query, string xml)
    {
        var hits = new List<FileHit>();
        var doc = XDocument.Parse(xml);
        foreach (var response in doc.Descendants().Where(e => e.Name.LocalName == "response"))
        {
            string href = response.Elements().FirstOrDefault(e => e.Name.LocalName == "href")?.Value.Trim() ?? "";
            string? path = InternalPathOf(href);
            if (string.IsNullOrEmpty(path) || path == "/")
                continue;

            var prop = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "prop");
            string name = path[(path.LastIndexOf('/') + 1)..];
            if (name.Length == 0 || !name.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            bool isDirectory = prop?.Descendants().Any(e => e.Name.LocalName == "collection") == true;
            DateTime? modified = null;
            string stamp = prop?.Descendants().FirstOrDefault(e => e.Name.LocalName == "getlastmodified")?.Value.Trim() ?? "";
            if (DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                modified = parsed.LocalDateTime;

            hits.Add(new FileHit
            {
                Name = name,
                InternalPath = path,
                LocalPath = "",              // not necessarily on this PC; "Open location" then opens it on the web
                Account = account.Account,
                ServerUrl = account.ServerUrl,
                IsDirectory = isDirectory,
                ExistsLocally = false,
                Modified = modified
            });
        }
        return hits;
    }

    /// <summary>
    /// "/remote.php/dav/files/USER/Dir/File%20name" -> "/Dir/File name" (the path inside the user's Nextcloud, as the
    /// sync journal paths are). Works for servers installed in a sub-directory too.
    /// </summary>
    internal static string? InternalPathOf(string href)
    {
        string path = href;
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
            path = uri.AbsolutePath;
        var match = Regex.Match(path, @"^.*?/remote\.php/dav/files/[^/]+(?<rest>/.*)?$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;
        string rest = match.Groups["rest"].Value.TrimEnd('/');
        if (rest.Length == 0)
            return "/";
        return "/" + string.Join("/", rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString));
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
