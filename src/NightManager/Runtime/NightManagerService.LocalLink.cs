using NightManager.Core.Contracts;
using NightManager.Core.Live;
using NightManager.Core.LocalLink;
using NightManager.Core.Settings;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// The local Wi-Fi link (docs/LOCAL-LINK.md): phones on this PC's home network get live data, sub previews and
    /// zoom crops straight from here; everyone else (collaborators away from home, the owner on mobile data) keeps
    /// using the server. Off for remote observatories (PC-wide option) and never reachable over a VPN.
    /// </summary>
    public sealed partial class NightManagerService {
        private const int LocalFramesKept = 2;
        private static readonly byte[] LocalLinkEntropy = Encoding.UTF8.GetBytes("NightManager.LocalLink.v1");

        private LocalLinkServer localLink;
        private X509Certificate2 localCertificate;
        private byte[] localKey;
        private byte[] localControlKey;
        /// <summary>The member-removal count the current key was made for (see <see cref="CheckLocalKeyEpoch"/>).</summary>
        private int localKeyEpoch;
        private IReadOnlyList<LocalAddress> localAddresses = [];
        private Timer localLiveTimer;
        private readonly LiveTelemetry localTelemetry = new();
        private int localClientsSeen;
        private int localPublishing;
        private string localFrameSent;
        /// <summary>The snapshot last sent whole over the link (field mode: the phone's view of the rig without internet).</summary>
        private ObservatorySnapshot localSnapshotSent;
        private static readonly System.Text.Json.JsonSerializerOptions LocalJson = new(System.Text.Json.JsonSerializerDefaults.Web) {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        private readonly object localFramesGate = new();
        private readonly List<(string Id, BitmapSource Image)> localFrames = [];
        private (string Id, byte[] Jpeg)? localPreview;
        private Timer localRestartDebounce;

        /// <summary>For the options page: what the local link is doing.</summary>
        public string LocalLinkStatus =>
            Installation.RemoteObservatory ? "Off: this is a remote observatory, so everything goes through the server."
            : localLink is null ? "Off (no home network found on this PC)."
            : $"On: phones on this PC's home network connect directly (port {localLink.Port}). {localLink.LiveClients} watching now.";

        private string LocalLinkPath => Path.Combine(RootDirectory, "local-link.bin");

        private void StartLocalLink() {
            if (!Settings.Enabled || Installation.RemoteObservatory || localLink is not null) {
                return;
            }
            try {
                LoadLocalIdentity();
                localAddresses = AddressPolicy.Listenable(LocalAdapters());
                if (localAddresses.Count == 0) {
                    NinaLog.Instance.Info("Local link: no home network adapter (Ethernet or Wi-Fi with a private address); off.");
                    return;
                }
                var server = new LocalLinkServer(localCertificate, localKey, new LocalSource(this), AllowsLocal, NinaLog.Instance, localControlKey);
                server.Start(localAddresses.Select(a => a.Address).Distinct());
                if (server.Port == 0) {
                    server.Dispose();
                    return;
                }
                localLink = server;
                localLiveTimer = new Timer(_ => PublishLocalLive(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            } catch (Exception ex) {
                NinaLog.Instance.Warning($"Local link not started: {ex.Message}");
                StopLocalLink();
            }
        }

        private void StopLocalLink() {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            localLiveTimer?.Dispose();
            localLiveTimer = null;
            localLink?.Dispose();
            localLink = null;
            lock (localFramesGate) {
                localFrames.Clear();
                localPreview = null;
            }
        }

        /// <summary>The PC joined or left a network (Wi-Fi reconnect, new address): start again on what's there now.</summary>
        private void OnNetworkAddressChanged(object sender, EventArgs e) {
            localRestartDebounce?.Dispose();
            localRestartDebounce = new Timer(_ => {
                StopLocalLink();
                StartLocalLink();
                sync?.Wake(); // tell the server the new addresses
            }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }

        /// <summary>PC-wide: a remote observatory switches the local link off on every NINA here.</summary>
        public void SetRemoteObservatory(bool remote) {
            var current = InstallationSettings.Load(InstallationSettingsPath);
            var merged = (current with { RemoteObservatory = remote }).Normalized();
            merged.Save(InstallationSettingsPath);
            Installation = merged;
            StopLocalLink();
            StartLocalLink();
            sync?.Wake();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>What the server passes to the observatory's members' phones; null while the link is off.</summary>
        private LocalLinkInfo LocalLinkInfo() => localLink is { } server && localCertificate is not null && localKey is not null
            ? new LocalLinkInfo {
                Port = server.Port,
                CertSha256 = LocalLinkIdentity.Fingerprint(localCertificate),
                Addresses = localAddresses.Select(a => a.Address.ToString()).Distinct().ToList(),
                Key = Convert.ToHexString(localKey).ToLowerInvariant(),
                ControlKey = localControlKey is null ? null : Convert.ToHexString(localControlKey).ToLowerInvariant(),
            }
            : null;

        private bool AllowsLocal(IPAddress remote, IPAddress local) {
            if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
            var adapter = localAddresses.FirstOrDefault(a => a.Address.Equals(local));
            return adapter is not null && AddressPolicy.Allows(remote, adapter);
        }

        /// <summary>
        /// The PC's physical network cards for a Night Manager Guard (it wakes this PC by MAC and pings it by address):
        /// up Ethernet and Wi-Fi adapters with a private IPv4 address, never VPNs; wired first.
        /// </summary>
        private static PcNetworkInfo PcNetwork() {
            try {
                var cards = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211)
                    .Select(n => (Nic: n, Address: n.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork && AddressPolicy.IsPrivate(u.Address))))
                    .Where(c => c.Address is not null && !AddressPolicy.LooksLikeVpn(new LocalAddress(c.Nic.Name, c.Nic.Description, AdapterKind.Ethernet, c.Address.Address, c.Address.PrefixLength)))
                    .OrderBy(c => c.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
                    .ToList();
                var macs = cards.Select(c => string.Join(":", c.Nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("x2")))).Where(m => m.Length == 17).Distinct().Take(6).ToList();
                return macs.Count == 0 ? null : new PcNetworkInfo { Macs = macs, Addresses = cards.Select(c => c.Address.Address.ToString()).Distinct().Take(8).ToList() };
            } catch (Exception) {
                return null;
            }
        }

        /// <summary>Up adapters with their IPv4 addresses (the policy picks the physical, private ones).</summary>
        private static IEnumerable<LocalAddress> LocalAdapters() {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)) {
                var kind = nic.NetworkInterfaceType switch {
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                        or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => AdapterKind.Ethernet,
                    NetworkInterfaceType.Wireless80211 => AdapterKind.Wireless,
                    NetworkInterfaceType.Tunnel => AdapterKind.Tunnel,
                    NetworkInterfaceType.Ppp => AdapterKind.Ppp,
                    NetworkInterfaceType.Loopback => AdapterKind.Loopback,
                    _ => AdapterKind.Other,
                };
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)) {
                    yield return new LocalAddress(nic.Name, nic.Description, kind, unicast.Address, unicast.PrefixLength);
                }
            }
        }

        /// <summary>The certificate and key, made once per PC and kept encrypted for this Windows user.</summary>
        private void LoadLocalIdentity() {
            if (localCertificate is not null && localKey is not null) return;
            try {
                if (File.Exists(LocalLinkPath)) {
                    var stored = JsonSerializer.Deserialize<StoredIdentity>(CredentialStore.UnprotectBytes(File.ReadAllBytes(LocalLinkPath), LocalLinkEntropy));
#pragma warning disable SYSLIB0057
                    localCertificate = new X509Certificate2(Convert.FromBase64String(stored.Pfx), (string)null, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
                    localKey = Convert.FromBase64String(stored.Key);
                    localKeyEpoch = stored.Epoch;
                    if (stored.ControlKey is { Length: > 0 } control) {
                        localControlKey = Convert.FromBase64String(control);
                    } else {
                        localControlKey = LocalLinkIdentity.CreateKey(); // made by an older build: add it now
                        SaveLocalIdentity();
                    }
                    return;
                }
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Local link identity unreadable, making a new one: {ex.Message}");
            }
            localCertificate = LocalLinkIdentity.CreateCertificate($"NightManager-{Environment.MachineName}");
            localKey = LocalLinkIdentity.CreateKey();
            localControlKey = LocalLinkIdentity.CreateKey();
            localKeyEpoch = sync?.LocalLinkEpoch ?? 0;
            SaveLocalIdentity();
        }

        private void SaveLocalIdentity() {
            var json = JsonSerializer.SerializeToUtf8Bytes(new StoredIdentity(Convert.ToBase64String(localCertificate.Export(X509ContentType.Pfx)), Convert.ToBase64String(localKey), localKeyEpoch,
                localControlKey is null ? null : Convert.ToBase64String(localControlKey)));
            Directory.CreateDirectory(RootDirectory);
            File.WriteAllBytes(LocalLinkPath, CredentialStore.ProtectBytes(json, LocalLinkEntropy));
        }

        /// <summary>
        /// Someone was removed from the observatory (the server's count went up): a new signing key, so their app can't
        /// connect locally any more. The new key reaches the remaining members through the server on the next check-in.
        /// </summary>
        private void CheckLocalKeyEpoch() {
            if (localLink is null || sync?.LocalLinkEpoch is not int epoch || epoch == localKeyEpoch) return;
            NinaLog.Instance.Info("Local link: a member was removed; making a new key.");
            localKey = LocalLinkIdentity.CreateKey();
            localControlKey = LocalLinkIdentity.CreateKey();
            localKeyEpoch = epoch;
            SaveLocalIdentity();
            StopLocalLink();
            StartLocalLink();
            sync?.Wake();
        }

        private sealed record StoredIdentity(string Pfx, string Key, int Epoch = 0, string ControlKey = null);

        /// <summary>Keeps NINA's rendered picture of the newest subs (two at most) for previews and zoom crops.</summary>
        private void RememberForLocalLink(string frameId, BitmapSource image) {
            if (localLink is null || image is null || !DataOn(DataFeatures.Frames)) return;
            try {
                if (!image.IsFrozen) {
                    if (!image.CanFreeze) return;
                    image = image.Clone();
                    image.Freeze();
                }
                lock (localFramesGate) {
                    localFrames.Insert(0, (frameId, image));
                    if (localFrames.Count > LocalFramesKept) localFrames.RemoveRange(LocalFramesKept, localFrames.Count - LocalFramesKept);
                    localPreview = null; // made when a phone asks
                }
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Local link: sub not kept: {ex.Message}");
            }
        }

        /// <summary>Every second, while a phone streams locally: the same updates as the live lane.</summary>
        private void PublishLocalLive() {
            CheckLocalKeyEpoch();
            var server = localLink;
            var snapshot = lastSnapshot;
            if (server is null || snapshot is null || server.LiveClients == 0 || !DataOn(DataFeatures.Live) || Interlocked.Exchange(ref localPublishing, 1) == 1) {
                localClientsSeen = server?.LiveClients ?? 0;
                return;
            }
            _ = Task.Run(async () => {
                try {
                    // A phone just connected: send everything, not just what changed.
                    if (server.LiveClients > localClientsSeen) {
                        localTelemetry.Reset();
                        localFrameSent = null;
                        localSnapshotSent = null;
                    }
                    localClientsSeen = server.LiveClients;
                    var update = localTelemetry.NextUpdate(snapshot, DateTimeOffset.UtcNow, LiveKeepAlive);
                    // "lf": the newest sub's id, so the phone fetches its preview as soon as it's saved.
                    if (LatestLocalFrameId is { } frame && frame != localFrameSent) {
                        update ??= new System.Text.Json.Nodes.JsonObject { ["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                        update["lf"] = frame;
                        localFrameSent = frame;
                    }
                    // "st": the whole state when it changed (devices, the sequence, the phone's control job), so the
                    // control sheet keeps working on a dark site with no internet.
                    if (!ReferenceEquals(snapshot, localSnapshotSent)) {
                        update ??= new System.Text.Json.Nodes.JsonObject { ["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                        update["st"] = System.Text.Json.JsonSerializer.SerializeToNode(snapshot, LocalJson);
                        localSnapshotSent = snapshot;
                    }
                    if (update is not null) {
                        await server.PublishAsync(update.ToJsonString()).ConfigureAwait(false);
                    }
                } catch (Exception ex) {
                    NinaLog.Instance.Debug($"Local live update not sent: {ex.Message}");
                } finally {
                    Volatile.Write(ref localPublishing, 0);
                }
            });
        }

        /// <summary>Guide steps for phones streaming locally.</summary>
        private void AddLocalGuideStep(LiveGuideStep step) {
            if (localLink?.LiveClients > 0 && DataOn(DataFeatures.Guiding)) {
                localTelemetry.AddGuideStep(step);
            }
        }

        /// <summary>What phones on the home network can ask for (read-only).</summary>
        private sealed class LocalSource(NightManagerService service) : ILocalLinkSource {
            public string HelloJson() => JsonSerializer.Serialize(new {
                observatoryId = service.sync?.ObservatoryId, rigId = service.RigId, rigName = service.RigName,
                pluginVersion = PluginVersion, frameId = service.LatestLocalFrameId,
            });

            public (string FrameId, byte[] Jpeg)? LatestPreview() {
                lock (service.localFramesGate) {
                    if (service.localPreview is { } ready) return ready;
                    if (service.localFrames.Count == 0) return null;
                    var (id, image) = service.localFrames[0];
                    var jpeg = ThumbnailEncoder.EncodePreview(image, PreviewPolicy.MaxPreviewEdge, PreviewPolicy.JpegQuality);
                    if (jpeg is null) return null;
                    service.localPreview = (id, jpeg);
                    return service.localPreview;
                }
            }

            /// <summary>Field mode: the same command path as from the server, so every check applies.</summary>
            public async Task<(bool Ok, string Message)> Command(string type, IReadOnlyDictionary<string, string> args) {
                if (type is not (CommandTypes.Equipment or CommandTypes.Move)) return (false, "Only equipment control goes over the home network.");
                if (service.commands is null) return (false, "The Night Manager isn't ready yet.");
                var now = DateTimeOffset.UtcNow;
                var command = new RemoteCommand {
                    Id = "local-" + Guid.NewGuid().ToString("N"), RigId = service.RigId, Type = type, IssuedAt = now, ExpiresAt = now.AddMinutes(2),
                    IssuedBy = "a phone on the home network", Args = args
                };
                var results = await service.commands.ProcessAsync([command], CancellationToken.None);
                var result = results.FirstOrDefault();
                return result is null ? (false, "Not run.") : (result.Outcome == CommandOutcomes.Executed, result.Message ?? result.Outcome);
            }

            public byte[] Crop(string frameId, double x, double y) {
                BitmapSource image;
                lock (service.localFramesGate) {
                    image = service.localFrames.FirstOrDefault(f => f.Id == frameId).Image;
                }
                if (image is null || PreviewPolicy.Crop(image.PixelWidth, image.PixelHeight, x, y) is not { } rect) return null;
                return ThumbnailEncoder.EncodeCrop(image, rect, PreviewPolicy.JpegQuality);
            }
        }

        private string LatestLocalFrameId {
            get { lock (localFramesGate) { return localFrames.Count > 0 ? localFrames[0].Id : null; } }
        }
    }
}
