using NightManager.Core.Commands;
using NightManager.Core.Contracts;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Core.Enum;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;

namespace NightManager.Plugin.Runtime {
    /// <summary>Maps remote actions onto NINA's own mediators, exactly as the UI buttons would.</summary>
    internal sealed class NinaCommandExecutor : ICommandExecutor {
        private static readonly TimeSpan WarmDuration = TimeSpan.FromMinutes(10);

        private readonly ISequenceMediator sequence;
        private readonly IGuiderMediator guider;
        private readonly ITelescopeMediator telescope;
        private readonly IDomeMediator dome;
        private readonly ICameraMediator camera;
        private readonly Func<ISequenceRootContainer> classicRoot;

        public NinaCommandExecutor(ISequenceMediator sequence, IGuiderMediator guider, ITelescopeMediator telescope, IDomeMediator dome, ICameraMediator camera,
            Func<ISequenceRootContainer> classicRoot = null) {
            this.classicRoot = classicRoot ?? (() => null);
            this.sequence = sequence;
            this.guider = guider;
            this.telescope = telescope;
            this.dome = dome;
            this.camera = camera;
        }

        public bool IsAvailable(string commandType) => commandType switch {
            CommandTypes.AbortSequence => true,
            CommandTypes.StopGuiding => guider.GetInfo()?.Connected == true,
            CommandTypes.ParkMount => telescope.GetInfo() is { Connected: true, CanPark: true },
            CommandTypes.CloseDome => dome.GetInfo() is { Connected: true, CanSetShutter: true },
            CommandTypes.WarmCamera => camera.GetInfo() is { Connected: true, CanSetTemperature: true },
            _ => true
        };

        public async Task AbortSequenceAsync(CancellationToken token) {
            if (!sequence.IsAdvancedSequenceRunning()) {
                await AbortClassicAsync(token);
                return;
            }

            sequence.CancelAdvancedSequence();

            // Wait briefly so the result reflects reality rather than a request.
            for (var i = 0; i < 30 && sequence.IsAdvancedSequenceRunning(); i++) {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }

            if (sequence.IsAdvancedSequenceRunning()) {
                throw new InvalidOperationException("Abort requested, but the sequence is still stopping.");
            }
        }

        /// <summary>
        /// The classic sequencer has no cancel on NINA's mediator; interrupting its root is how NINA itself stops a
        /// sequence ("Abort on error").
        /// </summary>
        private async Task AbortClassicAsync(CancellationToken token) {
            var root = classicRoot();
            if (root?.Status != SequenceEntityStatus.RUNNING) {
                return;
            }
            await root.Interrupt();
            for (var i = 0; i < 30 && root.Status == SequenceEntityStatus.RUNNING; i++) {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            if (root.Status == SequenceEntityStatus.RUNNING) {
                throw new InvalidOperationException("Abort requested, but the classic sequence is still stopping.");
            }
        }

        public async Task StopGuidingAsync(CancellationToken token) {
            if (!await guider.StopGuiding(token)) {
                throw new InvalidOperationException("The guider did not confirm that guiding stopped.");
            }
        }

        public async Task ParkMountAsync(CancellationToken token) {
            if (telescope.GetInfo()?.AtPark == true) {
                return;
            }

            if (!await telescope.ParkTelescope(new Progress<ApplicationStatus>(), token)) {
                throw new InvalidOperationException("The mount did not report that it parked.");
            }
        }

        public async Task CloseDomeAsync(CancellationToken token) {
            if (!await dome.CloseShutter(token)) {
                throw new InvalidOperationException("The dome did not report that the shutter closed.");
            }
        }

        public Task WarmCameraAsync(CancellationToken token) {
            // Warming takes minutes; start it and report immediately instead of blocking other commands.
            _ = Task.Run(async () => {
                try {
                    await camera.WarmCamera(WarmDuration, new Progress<ApplicationStatus>(), CancellationToken.None);
                } catch (Exception ex) {
                    NinaLog.Instance.Error("Remote camera warming failed", ex);
                }
            }, CancellationToken.None);

            return Task.CompletedTask;
        }
    }
}
