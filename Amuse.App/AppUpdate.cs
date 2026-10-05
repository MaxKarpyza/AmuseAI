using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace Amuse.App
{
    public sealed record AppUpdate
    {
        private readonly AppVersion _version;
        private readonly AppAsset _assetInstaller;
        private readonly AppAsset _assetStandalone;

        public AppUpdate(AppVersion version, string appPrefix)
        {
            _version = version;
            _assetInstaller = version.Assets?.FirstOrDefault(x => x.Name.StartsWith(appPrefix) && x.Name.EndsWith(".exe"));
            _assetStandalone = version.Assets?.FirstOrDefault(x => x.Name.StartsWith(appPrefix) && x.Name.EndsWith(".zip"));
        }

        public string Link => _version.Link;
        public string Version => _version.Version;
        public AppAsset AssetInstaller => _assetInstaller;
        public AppAsset AssetStandalone => _assetStandalone;

        /// <summary>
        /// Reports whether a release tag describes a build newer than the running one.
        /// </summary>
        /// <remarks>
        /// The comparison has to be a real version comparison, not a string inequality.
        /// The working tree can easily be ahead of the newest published release - a
        /// developer build of 3.8.2 against a latest release of 3.8.1 is not equal to
        /// it, but it is not an update either, and offering one would offer a
        /// downgrade. Unknown or unparsable tags are treated as "not an update" so a
        /// malformed response can never make the app offer to replace itself.
        /// </remarks>
        public static bool IsNewerThan(string candidateTag, string currentTag)
        {
            if (!TryParseTag(candidateTag, out System.Version candidate))
            {
                return false;
            }

            if (!TryParseTag(currentTag, out System.Version current))
            {
                return false;
            }

            return candidate > current;
        }

        private static bool TryParseTag(string tag, out System.Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return false;
            }

            // release tags are written "v3.8.1", the running version "3.8.2"
            string trimmed = tag.Trim().TrimStart('v', 'V');
            return System.Version.TryParse(trimmed, out version);
        }
    }

    public sealed record AppVersion
    {
        [JsonPropertyName("tag_name")]
        public string Version { get; set; }

        [JsonPropertyName("html_url")]
        public string Link { get; set; }

        [JsonPropertyName("assets")]
        public AppAsset[] Assets { get; set; }
    }

    public sealed record AppAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        public double DownloadSize => Size == 0 ? 0 : Size / Math.Pow(1024, 3);

        [JsonPropertyName("created_at")]
        public DateTime Created { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string DownloadLink { get; set; }
    }
}
