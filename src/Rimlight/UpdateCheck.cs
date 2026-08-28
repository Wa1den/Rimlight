using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Rimlight.Capture;
using Rimlight.Text;

namespace Rimlight;

/// <summary>
/// Asks GitHub once, at startup, whether a release newer than this build exists.
///
/// The releases API rather than the tag list: it answers with the one release marked
/// latest, skipping drafts and pre-releases without any of that having to be decided here.
/// Sixty unauthenticated calls an hour is the published limit, and this makes one per run.
/// </summary>
static class UpdateCheck
{
    const string Api = "https://api.github.com/repos/Wa1den/Rimlight/releases/latest";

    /// <summary>Where to send the user if the answer carried no link of its own.</summary>
    const string Page = "https://github.com/Wa1den/Rimlight/releases/latest";

    /// <summary>
    /// Long enough for a slow connection, short enough that a black hole is not waited on.
    /// Nothing is waiting for the answer, but a socket left open for minutes is still a
    /// socket left open.
    /// </summary>
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// The newer release and where to read about it, or null - which covers every way this
    /// can fail as well as the ordinary case of already being up to date. A failed check is
    /// not something to tell the user about: they did not ask a question.
    /// </summary>
    public static async Task<(Version Version, string Url)?> FindNewerAsync(Version current)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Api);

            // GitHub answers 403 to a request without a User-Agent, and this is the name
            // that ends up in their logs
            request.Headers.UserAgent.ParseAdd("Rimlight/" + Three(current));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Log($"{(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? Page : Page;

            if (!TryReadTag(tag, out var latest))
            {
                Log(Loc.P($"тег не разобран: {tag}", $"could not read the tag: {tag}"));
                return null;
            }

            return latest > Three(current) ? (latest, url) : null;
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Assembly versions carry four numbers and release tags carry three, and Version
    /// counts a missing fourth as lower than a zero - so 1.6.0 from a tag would read as
    /// older than 1.6.0.0 from the build. Both sides are cut to three.
    /// </summary>
    static Version Three(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static bool TryReadTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);

        string text = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text, out var parsed)) return false;

        version = Three(parsed);
        return true;
    }

    static void Log(string message) =>
        ProbeLog.Log(Loc.P("обновление", "update"),
                     Loc.P("проверка не удалась: ", "check failed: ") + message);
}
