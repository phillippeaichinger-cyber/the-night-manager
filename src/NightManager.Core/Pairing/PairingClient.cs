using NightManager.Core.Contracts;
using NightManager.Core.Json;
using NightManager.Core.Sync;

namespace NightManager.Core.Pairing;

public enum PairingState {
    Idle,
    Starting,
    WaitingForApproval,
    Approved,
    Expired,
    Failed
}

public sealed record PairingProgress(PairingState State, string? UserCode, DateTimeOffset? ExpiresAt, string? Message);

public sealed record PairingResult(string ApiKey, string? ObservatoryId, string? ObservatoryName);

/// <summary>
/// Device-code pairing (like signing in to a TV app): the plugin shows a short code, the user
/// types it into the phone app, and the backend hands this installation its credential.
/// No API keys are ever copied by hand.
/// </summary>
public sealed class PairingClient {
    private readonly IApiTransport transport;
    private readonly TimeProvider time;

    public PairingClient(IApiTransport transport, TimeProvider? time = null) {
        this.transport = transport;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Where to enter the code; the web address is shown only when the service offers a pairing page.</summary>
    public static string WaitingMessage(string? verificationUrl) {
        var page = Uri.TryCreate(verificationUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.GetLeftPart(UriPartial.Path)
            : null;
        // The dev plugin pairs with the dev server: its codes only work in NM Dev (or the dev website).
        var app = global::NightManager.Core.Settings.InstallationSettings.IsDevChannel ? "the NM Dev app (not Night Manager)" : "The Night Manager app";
        return page is null
            ? $"Open {app} → Add observatory, and enter this code."
            : $"Open {app} → Add observatory, or go to {page} in any browser, and enter this code.";
    }

    /// <summary>Runs one pairing attempt until approved, expired, failed or cancelled.</summary>
    public async Task<PairingResult?> PairAsync(PairStartRequest request, IProgress<PairingProgress> progress, CancellationToken token) {
        progress.Report(new PairingProgress(PairingState.Starting, null, null, "Requesting a pairing code…"));

        var started = await transport.PostJsonAsync(ApiPaths.PairStart, NmJson.Serialize(request), credential: null, token).ConfigureAwait(false);

        if (!started.IsSuccess || Parse<PairStartResponse>(started.Body) is not { } pairing) {
            progress.Report(new PairingProgress(PairingState.Failed, null, null, Describe(started, "Could not reach the Night Manager service.")));
            return null;
        }

        progress.Report(new PairingProgress(PairingState.WaitingForApproval, pairing.UserCode, pairing.ExpiresAt, WaitingMessage(pairing.VerificationUrl)));

        var poll = NmJson.Serialize(new PairPollRequest { PairingId = pairing.PairingId, PollToken = pairing.PollToken });
        var interval = TimeSpan.FromSeconds(Math.Clamp(pairing.PollIntervalSeconds, 2, 30));

        while (time.GetUtcNow() < pairing.ExpiresAt) {
            await Task.Delay(interval, time, token).ConfigureAwait(false);

            var polled = await transport.PostJsonAsync(ApiPaths.PairPoll, poll, credential: null, token).ConfigureAwait(false);

            if (!polled.IsSuccess) {
                if (polled.IsTransient) {
                    // Keep waiting through brief outages; the code stays valid server side.
                    continue;
                }

                progress.Report(new PairingProgress(PairingState.Failed, pairing.UserCode, pairing.ExpiresAt, Describe(polled, "Pairing failed.")));
                return null;
            }

            var status = Parse<PairPollResponse>(polled.Body);

            switch (status?.Status) {
                case PairStatuses.Approved when !string.IsNullOrEmpty(status.ApiKey):
                    progress.Report(new PairingProgress(PairingState.Approved, pairing.UserCode, pairing.ExpiresAt,
                        status.ObservatoryName is null ? "Paired."
                        : status.Reconnected == true ? $"Reconnected to your existing observatory “{status.ObservatoryName}”: its history and collaborators are kept."
                        : $"Paired: new observatory “{status.ObservatoryName}”."));
                    return new PairingResult(status.ApiKey, status.ObservatoryId, status.ObservatoryName);
                case PairStatuses.Denied:
                    progress.Report(new PairingProgress(PairingState.Failed, pairing.UserCode, pairing.ExpiresAt, "Pairing was declined in the app."));
                    return null;
                case PairStatuses.Expired:
                    progress.Report(new PairingProgress(PairingState.Expired, pairing.UserCode, pairing.ExpiresAt, "The code expired. Start pairing again."));
                    return null;
            }
        }

        progress.Report(new PairingProgress(PairingState.Expired, pairing.UserCode, pairing.ExpiresAt, "The code expired. Start pairing again."));
        return null;
    }

    private static T? Parse<T>(string? body) where T : class {
        if (string.IsNullOrWhiteSpace(body)) {
            return null;
        }

        try {
            return NmJson.Deserialize<T>(body);
        } catch (System.Text.Json.JsonException) {
            return null;
        }
    }

    private static string Describe(ApiResult result, string fallback) => result.StatusCode switch {
        0 => $"{fallback} Check the internet connection of this PC.",
        404 => "The Night Manager service doesn't support pairing for this plugin version yet. Check for a plugin update or try again later.",
        429 => "Too many pairing attempts. Wait a minute and try again.",
        >= 500 => "The Night Manager service is having problems. Try again in a few minutes.",
        _ => $"{fallback} (HTTP {result.StatusCode})"
    };
}
