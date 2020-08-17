#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Providers.Plugins.Tmdb.Models.Movies;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.Plugins.Tmdb.Movies
{
    /// <summary>
    /// Class MovieDbProvider.
    /// </summary>
    public class TmdbMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
    {
        internal static TmdbMovieProvider Current { get; private set; }

        private readonly IJsonSerializer _jsonSerializer;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IFileSystem _fileSystem;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly ILogger<TmdbMovieProvider> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationHost _appHost;

        private readonly CultureInfo _usCulture = new CultureInfo("en-US");

        public TmdbMovieProvider(
            IJsonSerializer jsonSerializer,
            IHttpClientFactory httpClientFactory,
            IFileSystem fileSystem,
            IServerConfigurationManager configurationManager,
            ILogger<TmdbMovieProvider> logger,
            ILibraryManager libraryManager,
            IApplicationHost appHost)
        {
            _jsonSerializer = jsonSerializer;
            _httpClientFactory = httpClientFactory;
            _fileSystem = fileSystem;
            _configurationManager = configurationManager;
            _logger = logger;
            _libraryManager = libraryManager;
            _appHost = appHost;
            Current = this;
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            return GetMovieSearchResults(searchInfo, cancellationToken);
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetMovieSearchResults(ItemLookupInfo searchInfo, CancellationToken cancellationToken)
        {
            var tmdbId = searchInfo.GetProviderId(MetadataProvider.Tmdb);

            if (!string.IsNullOrEmpty(tmdbId))
            {
                cancellationToken.ThrowIfCancellationRequested();

                await EnsureMovieInfo(tmdbId, searchInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);

                var dataFilePath = GetDataFilePath(tmdbId, searchInfo.MetadataLanguage);

                var obj = _jsonSerializer.DeserializeFromFile<MovieResult>(dataFilePath);

                var tmdbSettings = await GetTmdbSettings(cancellationToken).ConfigureAwait(false);

                var tmdbImageUrl = tmdbSettings.images.GetImageUrl("original");

                var remoteResult = new RemoteSearchResult
                {
                    Name = obj.GetTitle(),
                    SearchProviderName = Name,
                    ImageUrl = string.IsNullOrWhiteSpace(obj.Poster_Path) ? null : tmdbImageUrl + obj.Poster_Path
                };

                if (!string.IsNullOrWhiteSpace(obj.Release_Date))
                {
                    // These dates are always in this exact format
                    if (DateTime.TryParse(obj.Release_Date, _usCulture, DateTimeStyles.None, out var r))
                    {
                        remoteResult.PremiereDate = r.ToUniversalTime();
                        remoteResult.ProductionYear = remoteResult.PremiereDate.Value.Year;
                    }
                }

                remoteResult.SetProviderId(MetadataProvider.Tmdb, obj.Id.ToString(_usCulture));

                if (!string.IsNullOrWhiteSpace(obj.Imdb_Id))
                {
                    remoteResult.SetProviderId(MetadataProvider.Imdb, obj.Imdb_Id);
                }

                return new[] { remoteResult };
            }

            return await new TmdbSearch(_logger, _jsonSerializer, _libraryManager).GetMovieSearchResults(searchInfo, cancellationToken).ConfigureAwait(false);
        }

        public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            return GetItemMetadata<Movie>(info, cancellationToken);
        }

        public Task<MetadataResult<T>> GetItemMetadata<T>(ItemLookupInfo id, CancellationToken cancellationToken)
            where T : BaseItem, new()
        {
            var movieDb = new GenericTmdbMovieInfo<T>(_logger, _jsonSerializer, _libraryManager, _fileSystem);

            return movieDb.GetMetadata(id, cancellationToken);
        }

        public string Name => TmdbUtils.ProviderName;

        /// <summary>
        /// The _TMDB settings task.
        /// </summary>
        private TmdbSettingsResult _tmdbSettings;

        /// <summary>
        /// Gets the TMDB settings.
        /// </summary>
        /// <returns>Task{TmdbSettingsResult}.</returns>
        internal async Task<TmdbSettingsResult> GetTmdbSettings(CancellationToken cancellationToken)
        {
            if (_tmdbSettings != null)
            {
                return _tmdbSettings;
            }

            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, string.Format(CultureInfo.InvariantCulture, TmdbConfigUrl, TmdbUtils.ApiKey));
            requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(TmdbUtils.AcceptHeader));
            using var response = await GetMovieDbResponse(requestMessage).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            _tmdbSettings = await _jsonSerializer.DeserializeFromStreamAsync<TmdbSettingsResult>(stream).ConfigureAwait(false);
            return _tmdbSettings;
        }

        private const string TmdbConfigUrl = TmdbUtils.BaseTmdbApiUrl + "3/configuration?api_key={0}";
        private const string GetMovieInfo3 = TmdbUtils.BaseTmdbApiUrl + @"3/movie/{0}?api_key={1}&append_to_response=casts,releases,images,keywords,trailers";

        /// <summary>
        /// Gets the movie data path.
        /// </summary>
        /// <param name="appPaths">The app paths.</param>
        /// <param name="tmdbId">The TMDB id.</param>
        /// <returns>System.String.</returns>
        internal static string GetMovieDataPath(IApplicationPaths appPaths, string tmdbId)
        {
            var dataPath = GetMoviesDataPath(appPaths);

            return Path.Combine(dataPath, tmdbId);
        }

        internal static string GetMoviesDataPath(IApplicationPaths appPaths)
        {
            var dataPath = Path.Combine(appPaths.CachePath, "tmdb-movies2");

            return dataPath;
        }

        /// <summary>
        /// Downloads the movie info.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="preferredMetadataLanguage">The preferred metadata language.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        internal async Task DownloadMovieInfo(string id, string preferredMetadataLanguage, CancellationToken cancellationToken)
        {
            var mainResult = await FetchMainResult(id, true, preferredMetadataLanguage, cancellationToken).ConfigureAwait(false);

            if (mainResult == null)
            {
                return;
            }

            var dataFilePath = GetDataFilePath(id, preferredMetadataLanguage);

            Directory.CreateDirectory(Path.GetDirectoryName(dataFilePath));

            _jsonSerializer.SerializeToFile(mainResult, dataFilePath);
        }

        internal Task EnsureMovieInfo(string tmdbId, string language, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(tmdbId))
            {
                throw new ArgumentNullException(nameof(tmdbId));
            }

            var path = GetDataFilePath(tmdbId, language);

            var fileInfo = _fileSystem.GetFileSystemInfo(path);

            if (fileInfo.Exists)
            {
                // If it's recent or automatic updates are enabled, don't re-download
                if ((DateTime.UtcNow - _fileSystem.GetLastWriteTimeUtc(fileInfo)).TotalDays <= 2)
                {
                    return Task.CompletedTask;
                }
            }

            return DownloadMovieInfo(tmdbId, language, cancellationToken);
        }

        internal string GetDataFilePath(string tmdbId, string preferredLanguage)
        {
            if (string.IsNullOrEmpty(tmdbId))
            {
                throw new ArgumentNullException(nameof(tmdbId));
            }

            var path = GetMovieDataPath(_configurationManager.ApplicationPaths, tmdbId);

            if (string.IsNullOrWhiteSpace(preferredLanguage))
            {
                preferredLanguage = "alllang";
            }

            var filename = string.Format(CultureInfo.InvariantCulture, "all-{0}.json", preferredLanguage);

            return Path.Combine(path, filename);
        }

        public static string GetImageLanguagesParam(string preferredLanguage)
        {
            var languages = new List<string>();

            if (!string.IsNullOrEmpty(preferredLanguage))
            {
                preferredLanguage = NormalizeLanguage(preferredLanguage);

                languages.Add(preferredLanguage);

                if (preferredLanguage.Length == 5) // like en-US
                {
                    // Currenty, TMDB supports 2-letter language codes only
                    // They are planning to change this in the future, thus we're
                    // supplying both codes if we're having a 5-letter code.
                    languages.Add(preferredLanguage.Substring(0, 2));
                }
            }

            languages.Add("null");

            if (!string.Equals(preferredLanguage, "en", StringComparison.OrdinalIgnoreCase))
            {
                languages.Add("en");
            }

            return string.Join(",", languages);
        }

        public static string NormalizeLanguage(string language)
        {
            if (!string.IsNullOrEmpty(language))
            {
                // They require this to be uppercase
                // Everything after the hyphen must be written in uppercase due to a way TMDB wrote their api.
                // See here: https://www.themoviedb.org/talk/5119221d760ee36c642af4ad?page=3#56e372a0c3a3685a9e0019ab
                var parts = language.Split('-');

                if (parts.Length == 2)
                {
                    language = parts[0] + "-" + parts[1].ToUpperInvariant();
                }
            }

            return language;
        }

        public static string AdjustImageLanguage(string imageLanguage, string requestLanguage)
        {
            if (!string.IsNullOrEmpty(imageLanguage)
                && !string.IsNullOrEmpty(requestLanguage)
                && requestLanguage.Length > 2
                && imageLanguage.Length == 2
                && requestLanguage.StartsWith(imageLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return requestLanguage;
            }

            return imageLanguage;
        }

        /// <summary>
        /// Fetches the main result.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="isTmdbId">if set to <c>true</c> [is TMDB identifier].</param>
        /// <param name="language">The language.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{CompleteMovieData}.</returns>
        internal async Task<MovieResult> FetchMainResult(string id, bool isTmdbId, string language, CancellationToken cancellationToken)
        {
            var url = string.Format(CultureInfo.InvariantCulture, GetMovieInfo3, id, TmdbUtils.ApiKey);

            if (!string.IsNullOrEmpty(language))
            {
                url += string.Format(CultureInfo.InvariantCulture, "&language={0}", NormalizeLanguage(language));

                // Get images in english and with no language
                url += "&include_image_language=" + GetImageLanguagesParam(language);
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
            requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(TmdbUtils.AcceptHeader));
            using var mainResponse = await GetMovieDbResponse(requestMessage);
            if (mainResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await using var stream = await mainResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var mainResult = await _jsonSerializer.DeserializeFromStreamAsync<MovieResult>(stream).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // If the language preference isn't english, then have the overview fallback to english if it's blank
            if (mainResult != null &&
                string.IsNullOrEmpty(mainResult.Overview) &&
                !string.IsNullOrEmpty(language) &&
                !string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("MovieDbProvider couldn't find meta for language " + language + ". Trying English...");

                url = string.Format(CultureInfo.InvariantCulture, GetMovieInfo3, id, TmdbUtils.ApiKey) + "&language=en";

                if (!string.IsNullOrEmpty(language))
                {
                    // Get images in english and with no language
                    url += "&include_image_language=" + GetImageLanguagesParam(language);
                }

                using var langRequestMessage = new HttpRequestMessage(HttpMethod.Get, url);
                langRequestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(TmdbUtils.AcceptHeader));
                using var langResponse = await GetMovieDbResponse(langRequestMessage);

                await using var langStream = await langResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var langResult = await _jsonSerializer.DeserializeFromStreamAsync<MovieResult>(stream).ConfigureAwait(false);
                mainResult.Overview = langResult.Overview;
            }

            return mainResult;
        }

        /// <summary>
        /// Gets the movie db response.
        /// </summary>
        internal async Task<HttpResponseMessage> GetMovieDbResponse(HttpRequestMessage message)
        {
            message.Headers.UserAgent.Add(new ProductInfoHeaderValue(_appHost.ApplicationUserAgent));
            return await _httpClientFactory.CreateClient().SendAsync(message);
        }

        /// <inheritdoc />
        public int Order => 1;

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClientFactory.CreateClient().GetAsync(url, cancellationToken);
        }
    }
}
