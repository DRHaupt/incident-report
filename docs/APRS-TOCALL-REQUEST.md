# APRS device identifier (tocall) request – Field Map

Every APRS packet Field Map sends uses a destination address ("tocall") that identifies the software. Until a
permanent one is allocated, Field Map uses **`APZFMP`** from the experimental `APZxxx` block
(`AprsBeacon.ToCall` in `src/FieldMap.Core/Aprs/AprsBeacon.cs`).

Allocations are made in the APRS device identification database maintained at
<https://github.com/aprsorg/aprs-deviceid> (the successor to the list kept by Bob Bruninga, WB4APR). Requests are
filed as GitHub issues there.

## How to submit

1. Sign in to GitHub and open <https://github.com/aprsorg/aprs-deviceid/issues/new>.
2. Paste the title and body below. Fill in the contact e-mail.
3. When the maintainers allocate a tocall (they may suggest a different one), change `ToCall` in
   `AprsBeacon.cs` (and the test that checks it), rebuild, and update this file.

The proposed `APFMP?` was free when checked against `tocalls.yaml` on 3 October 2026 (the nearest entries are
`APFMN?` and `APFMO?`, a different product). `?` is a version digit, so Field Map 1.x would send `APFMP1`.

---

**Title:** New tocall request: APFMP? (Field Map, offline emergency map for Windows)

**Body:**

```
Hello,

I'd like to request a tocall for Field Map, an offline emergency-response map for Windows
(in-vehicle Toughbook laptops) used in British Columbia and Alberta, Canada. Besides its own
map features it shows APRS stations from APRS-IS and/or a KISS TNC, sends the user's position
(SmartBeaconing or fixed interval) and supports APRS messaging with acks and reply-acks.

Suggested tocall: APFMP?   (? = major version digit, e.g. APFMP1)

  - tocall: APFMP?
    vendor: Field Map project
    model: Field Map
    class: software
    os: Windows
    contact: VA7DHR <e-mail address>

Project: https://github.com/DRHaupt/incident-report

APRS-IS logins identify as "vers FieldMap <version>"; it uses the filtered port 14580 and
only sends with a verified passcode.

Thanks,
VA7DHR
```
