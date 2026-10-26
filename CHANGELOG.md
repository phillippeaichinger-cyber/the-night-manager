# Changelog

## 2.1.0 (24 Oct 2026)

- **Home Wi-Fi link:** when your phone is on the same home network as the PC, it gets live data and full-size
  previews straight from the PC (HTTPS, pinned certificate, signed requests; never over a VPN), and keeps working
  without internet. Can be switched off for remote observatories.
- **Startup helper** (optional, off until you turn it on): opens NINA again after a power cut or restart, with the
  startup plan chosen in the app, and applies the Syncthing upload window at logon.
- Syncthing: pause, resume and "sync now" from the app act straight away.
- The startup plan lists the profile that's open in NINA (it was missed while NINA held it open).
- The night ends at astronomical dawn once the last subs and dawn flats are in, even while a looping sequence runs.
- Re-pairing says whether the PC reconnected to its existing observatory or made a new one.

## 2.0.1 (22 Oct 2026)

- Rig equipment (telescope, focal length and ratio, aperture, sensor, focuser step, filter wheel) is read from the
  NINA profile for the focus and optics models.

## 2.0.0 (15 Oct 2026)

- Observatory cameras in two sections: **pier cameras** (the kit: pictures on a schedule and on any warning or
  alarm) and **all-sky cameras** (the sky, run by their own software: pictures on a schedule and when the safety
  status changes). Existing cameras ticked "all-sky" move to that section by themselves.
- Pier cameras can be a **ZWO camera plugged into the PC** (not NINA's or the guide camera), through ZWO's driver that
  NINA installs: one short exposure in memory, shrunk, sent and never saved. Daylight and darkness settings switch at
  nautical twilight and find their own exposure; after dark it never exceeds the cap (default 2 s, at most 10 s).

Complete rebuild for NINA 3.3 / .NET 10.

- Pair with a short code from the app; no API keys to copy.
- API v2: one signed, batched, idempotent ingest endpoint; the secret never leaves the PC.
- Durable on-disk outbox: no data lost during internet outages or NINA restarts.
- Strongly typed, push-based equipment state (replaces 10 s reflection polling).
- Timeline events for sequence, target, filter, flips, autofocus, dithers, guiding, parking, dome, devices and failures.
- Sub grading with reasons, plus a cull list written next to the images at the end of the night.
- Night Watch incidents: clouds, focus drift, dew risk, guiding degraded, cooling lost, no frames.
- Plugin-owned sessions with per-target / per-filter totals (fixes always-zero exposure totals).
- Thumbnails from NINA's rendered preview (works for XISF; no SkiaSharp/SQLite native dependencies).
- Remote actions with an owner-controlled allow-list, expiry, de-duplication and confirmation.
- Sequencer instructions: "Notify my phone", "End night & send report".
- Real twilight times from NINA's ephemeris.
