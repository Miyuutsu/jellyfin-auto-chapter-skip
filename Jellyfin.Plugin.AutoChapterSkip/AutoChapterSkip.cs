using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoChapterSkip
{
    /// <summary>
    /// Automatically skip chapters matching regex.
    /// Commands clients to seek to the end of matched chapters as soon as they start playing them.
    /// </summary>
    public class AutoChapterSkip : IHostedService, IDisposable
    {
        private readonly object _currentPositionsLock = new();
        private readonly Dictionary<string, long?> _currentPositions;
        private readonly ISessionManager _sessionManager;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AutoChapterSkip> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AutoChapterSkip"/> class.
        /// </summary>
        /// <param name="sessionManager">Session manager.</param>
        /// <param name="serviceProvider">Service provider.</param>
        /// <param name="logger">Logger instance.</param>
        public AutoChapterSkip(
            ISessionManager sessionManager,
            IServiceProvider serviceProvider,
            ILogger<AutoChapterSkip> logger)
        {
            _currentPositions = new Dictionary<string, long?>();
            _sessionManager = sessionManager;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        /// <summary>
        /// Starts the service.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackStopped += SessionManager_PlaybackStopped;
            _sessionManager.PlaybackProgress += SessionManager_PlaybackProgress;

            return Task.CompletedTask;
        }

        /// <summary>
        /// Stops the service.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackStopped -= SessionManager_PlaybackStopped;
            _sessionManager.PlaybackProgress -= SessionManager_PlaybackProgress;

            return Task.CompletedTask;
        }

        private void SessionManager_PlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
            var match = Plugin.Instance?.Configuration.Match;

            if (string.IsNullOrEmpty(match) || e.Session == null || e.Item == null)
            {
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManager>();
            var dtoService = scope.ServiceProvider.GetRequiredService<IDtoService>();

            var user = userManager.GetUserById(e.Session.UserId);
            if (user == null)
            {
                return;
            }

            var dtoOptions = new DtoOptions { Fields = new[] { ItemFields.Chapters } };
            var itemDto = dtoService.GetBaseItemDto(e.Item, dtoOptions, user);
            var chapters = itemDto.Chapters;

            if (chapters == null || chapters.Count == 0)
            {
                return;
            }

            var currentTicks = e.PlaybackPositionTicks.GetValueOrDefault();
            var regex = new Regex(match);
            var chapter = chapters.LastOrDefault(c => c.StartPositionTicks <= currentTicks);

            if (chapter == null || !regex.IsMatch(chapter.Name ?? string.Empty))
            {
                return;
            }

            var send = (long? ticks) =>
            {
                Lock(() => _currentPositions[e.Session.Id] = ticks);

                // This is the ONLY log left. It will only print once per successful skip.
                _logger.LogInformation(
                    "AutoChapterSkip: Skipped chapter '{ChapterName}' in '{ItemName}'.",
                    chapter.Name,
                    e.Item.Name);

                _sessionManager.SendPlaystateCommand(
                    e.Session.Id,
                    e.Session.Id,
                    new PlaystateRequest
                    {
                        Command = PlaystateCommand.Seek,
                        ControllingUserId = e.Session.UserId.ToString("N"),
                        SeekPositionTicks = ticks
                    },
                    CancellationToken.None);
            };

            var remainingChapters = chapters.Skip(chapters.IndexOf(chapter) + 1).ToList();
            var nextChapter = remainingChapters.FirstOrDefault(c => !regex.IsMatch(c.Name ?? string.Empty));
            var nextChapterTicks = nextChapter?.StartPositionTicks;

            if (nextChapterTicks == null)
            {
                var runTimeTicks = e.Item.RunTimeTicks.GetValueOrDefault();
                if (!remainingChapters.Any(c => !regex.IsMatch(c.Name ?? string.Empty)) && currentTicks < runTimeTicks)
                {
                    send(runTimeTicks);
                }

                return;
            }

            long? previousChapterTicks = null;
            Lock(() => _currentPositions.TryGetValue(e.Session.Id, out previousChapterTicks));

            if (currentTicks <= previousChapterTicks)
            {
                return;
            }

            send(nextChapterTicks);
        }

        private void SessionManager_PlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            Lock(() => _currentPositions.Remove(e.Session.Id));
        }

        private void Lock(Action work)
        {
            lock (_currentPositionsLock)
            {
                work();
            }
        }

        /// <summary>
        /// Dispose.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose.
        /// </summary>
        /// <param name="disposing">Dispose.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            _sessionManager.PlaybackStopped -= SessionManager_PlaybackStopped;
            _sessionManager.PlaybackProgress -= SessionManager_PlaybackProgress;
        }
    }
}
