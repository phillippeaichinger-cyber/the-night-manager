The Night Manager puts your NINA observatory on your phone (iPhone and Android): every sub as it lands with a small preview, its HFR, star count and a grade; the guiding graph; NINA's status and the sequence's progress; and alerts only when something actually needs you (guiding lost, unsafe conditions, the sequence stopping, the camera not reaching temperature, clouds, focus drift, dew). In the morning there's a summary of the night and why any subs were soft.

## How it works
This plugin is the open-source NINA side. It reads NINA's state through the plugin API and sends it to The Night Manager service, which relays it to the app. It needs the free app and an account: pair from *Options → The Night Manager*, then enter the code in the app or on the pairing page in any browser.

- Works with the Advanced and classic sequencers, Target Scheduler, PHD2 and Hocus Focus.
- Sequence instructions: Notify my phone, Ask my phone, Wait for go, Wake me up, Send latest image, Note to timeline, Night ending and Send night report.
- Remote actions (pause, abort, park, close the dome, warm the camera, safe shutdown) are off until you allow them in the options. Each must be confirmed on the phone, and NINA carries it out the normal way.
- Your FITS/XISF files never leave the PC: only small previews and the numbers are sent. On the same home Wi-Fi, the phone can get live data straight from the PC.
- A cull list (grades and rejects) is written next to your images at the end of the night. Nothing is ever deleted or moved.

## Startup helper (optional)
The package includes `Launcher/NightManager.Launcher.exe`. It's off until you turn it on in the options. When on, it's registered at Windows logon and reopens NINA after a power cut or restart, with the plan you chose in the app. Turning it off removes the registration.

## Free and Pro
The plugin is free. The app is free for one rig: live view, every alert (safety ones included), the remote actions and a few AI night reports a month. An optional Pro subscription adds several rigs and sites in one app, sharing, coaching and nightly reports, and pays for running the service for everyone. NINA's own functions, and data from other free plugins such as Target Scheduler, are never behind the paywall.

## Privacy
Sent: equipment state, events, frame numbers, optional small previews and file names. Never sent: images or full paths. During the beta, anonymous focus and optics measurements help build new features; switch them off under *Data and privacy*. Privacy policy: https://www.ninanightagent.com/privacy

## Help
Guide: https://www.ninanightagent.com/guide · Questions and bug reports: the Night Manager Discord (https://discord.gg/yz2RNBPYYk), with NINA's log. This plugin is not supported by the N.I.N.A. team.

Developed with AI assistance (Anthropic's Claude), directed and maintained by Phil Aichinger. Source: https://github.com/phillippeaichinger-cyber/the-night-manager (MPL-2.0).
