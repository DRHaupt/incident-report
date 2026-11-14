# Field Suite for iOS and Android (installed app)

The same screens as [Field Map for phones](FIELDMAP-WEB.md), packaged as a real app with
[Capacitor](https://capacitorjs.com) 8 (MIT licence). The app's files are inside the app; the map, search, nearest,
directions, team and live data still come from **your Field Map server**. Each phone is told the server's address
once, on first start.

The project is in `mobile/`. There is no second copy of the app's code: `mobile/scripts/copy-web.mjs` copies
`src/FieldMap.Web/wwwroot` into the Android and Xcode projects, so every change to the web version is in the next
app build too.

## The home screen

The app is called **Field Suite** and opens on a home screen with four tiles:

| Tile | What it opens |
|---|---|
| **Map** | Field Map: search, nearest, directions, layers, event files, team, live hazards |
| **First aid & CPR** | The first aid topics, timers and CPR helper, full screen |
| **Hazmat** | ERG 2024 lookup, full screen (drawing zones switches to the map) |
| **Incident reports** | The Incident Report Manager's forms, filled in on the phone (below) |

Under the tiles: **Team** (sharing on or off), **Send my location** and, when team features are on, **SOS**.
A tool opened from the home screen fills the screen; its X (or Android's Back) returns to the home screen. From the
map, the home button beside the search box does the same. The browser version still opens on the map and has the
same home button.

## Incident reports on the phone

The forms are the **same 96 templates as the Windows Incident Report Manager**: the server compiles them in from
`src/IncidentReporting/Templates` and serves them at `/api/forms`, so a form changed there changes on the phones
with the next server update. The phone downloads the catalogue once and keeps it for use with no signal.

- **New report**: find a form by name or category. Date and time of the incident start as now, "Reported by"
  as the team name, and **Use my position** fills in the address with coordinates and MGRS.
- Reports are **saved on the phone as you type**. **Mark complete** lists any required fields still empty.
  Editing a complete report makes it a draft again.
- **Share** sends the report as text through the share sheet (email, messages, any app). Values use the same
  formats as the Windows app.
- Reports are **kept on that phone only**. Nothing is sent to the server, and there is no import into the
  Windows Incident Report Manager yet. Removing the app, or clearing its data, removes them. Many of the forms
  hold personal or health information: until the organisation has decided where such reports may be stored
  (see LEGAL-NOTES.md), sharing one is a deliberate act by the person holding the phone.

## Position sharing in the background

With **Share my position** on (Team), the installed app keeps sending the phone's position while the app is in
the background or the screen is locked, until sharing is turned off or the app is closed (swiped away).

- **iPhone** asks for location "While Using", and later offers "Always"; the blue location indicator shows
  while it runs.
- **Android** shows a permanent "Field Suite is sharing your position" notification while it runs, as Android
  requires. If the phone's battery saver stops the app anyway, exclude Field Suite from battery optimisation.
- It uses more battery than leaving the app closed: GPS stays on and the team is checked every 15 seconds.

## Notifications

While the app is in the background **with sharing on**, a **team SOS** or an **APRS station reporting an
emergency** raises a notification with sound, on the lock screen too. Tapping it opens the alert on the map.
With the app on screen the red banner, tone and vibration are used as before.

These are notifications made by the app itself while it is running. They are **not push notifications**: once
the app has been closed, or with sharing off and the app in the background, nothing arrives. Real push (alerts
reaching a phone whose app is closed) needs things only the organisation can set up: an Apple push key from an
Apple Developer account, a Firebase project for Android, and those credentials on the Field Map server so it can
send. The app and server parts for that are not built yet.

## What's different from the web version

| | Web version (Safari/Chrome, Home Screen) | Installed app |
|---|---|---|
| Where the app's files are | On the server, kept by the browser | In the app: it starts with no signal |
| Server | The site it was opened from | Asked on first start; change it under More → Server |
| Sign-in (access code) | Cookie | A token the app keeps and sends with each request |
| Map with no signal | Areas viewed or saved (browser storage) | The same, kept in the app's own storage (up to 12,000 tiles) |
| Map info, hazmat (ERG), first aid, event files with no signal | Last copy | Last copy |
| Keep the screen on | Browser wake lock, where supported | The phone's own setting, always works |
| Send my location | Share sheet on iPhone; copy on some Android browsers | Share sheet on both |
| Android Back button | Leaves the page | One step back in the panel, then closes it, then leaves |
| Starts on | The map | The Field Suite home screen |
| Sharing position with the app in the background | No | Yes, while sharing is on |
| Alerts with the app in the background | No | Notifications, while sharing is on |

Everything else (search, nearest, directions, layers, event files, team, SOS, hazmat, first aid, APRS, live
hazards, imagery, themes, fold-aware layout) is the same code.

## On the server

Update the server to this version or newer. Two things changed for the apps:

- **Sign-in token.** A browser inside an app can't use the server's cookie (to it the server is another site), so
  the app asks `/api/login` for a token and sends it as `Authorization: Bearer …`. It's the same value the cookie
  holds, so changing the access code signs the apps out too.
- **`AppOrigins`** in `/etc/fieldmap-web/appsettings.json` lists who may call the server from another site. The
  default, `["capacitor://localhost", "https://localhost"]`, is exactly the iOS and the Android app. Set it to `[]`
  to refuse the installed apps. No other website is given access.

The server must be reachable over **HTTPS** (the Cloudflare Tunnel or Caddy set-up in FIELDMAP-WEB.md): phones
refuse plain http from inside an app. Behind **Cloudflare Access**, the app can't show Cloudflare's sign-in page,
so either leave Access off for the server's hostname and rely on the access code, or add a bypass for it.

## Building

You need Node 22. For Android: Android Studio (or JDK 21 and the Android SDK). For iOS: a Mac with Xcode 26.

```sh
cd mobile
npm ci
npm run sync        # copies the web app and updates both native projects
npx cap open android    # or: npx cap open ios
```

Then Run in Android Studio or Xcode. From the command line, `cd android && ./gradlew assembleDebug` writes
`app/build/outputs/apk/debug/app-debug.apk`.

**GitHub Actions** (`.github/workflows/mobile.yml`) builds both whenever `mobile/` or the web app changes:

- **Android**: the APK is an artifact of the run (`FieldSuite-android-<version>`, kept 14 days). Copy it to a phone
  and open it to install ("Install unknown apps" must be allowed for the app you open it with).
- **iOS**: compiled for the simulator only, to prove the project builds. Apple doesn't allow installing on
  iPhones without signing (below).

## Before giving it to crews

These are decisions and accounts only the organisation can make, so they are not set up:

1. **App ID.** `org.fieldsuite.app` is a placeholder. Pick your own reverse-domain ID before the first
   release, because it can never change afterwards without becoming a different app. It's set in
   `mobile/capacitor.config.json`, `mobile/android/app/build.gradle` (`namespace`, `applicationId`), the
   `android/app/src/main/java/...` folder and `package` line of `MainActivity.java`, `android/app/src/main/res/values/strings.xml`
   and `PRODUCT_BUNDLE_IDENTIFIER` in the Xcode project.
2. **Android signing key.** Without one, each CI build is signed with a throw-away key and can't be installed
   over the last one. Create a key once and keep a safe copy (losing it means every phone must reinstall):

   ```sh
   keytool -genkeypair -v -keystore fieldmap.keystore -alias fieldmap -keyalg RSA -keysize 4096 -validity 10000
   ```

   ```sh
   base64 -w0 fieldmap.keystore
   ```

   Add the output as the repository secret `ANDROID_KEYSTORE_BASE64` and the password as
   `ANDROID_KEYSTORE_PASSWORD`. CI then signs every APK with it, numbered so each installs over the one before.
3. **iPhone distribution** needs an Apple Developer Program membership (paid, yearly). In Xcode choose your team
   under Signing & Capabilities, then Product → Archive for TestFlight or the App Store. For crews only,
   TestFlight or an unlisted App Store app avoids a public listing.
4. **Google Play** (optional; the APK can be handed out directly) needs a Play Console account and a release
   build: `./gradlew bundleRelease` with the signing key above.
5. **Store review.** Both stores ask what location is used for (the text is in `Info.plist` and the Android
   manifest) and want a privacy policy. Background location gets extra scrutiny: Apple wants the reason shown
   to the user (it is) and Google Play wants a declaration and a short video for the "location" foreground
   service. The app sends position only to the server address the user entered, and only while sharing is on.

## Icons and splash screen

Made from `assets/fieldmap/fieldmap.svg`. After changing the logo:

```sh
node scripts/render-icons.mjs
```

```sh
npx @capacitor/assets generate --ios --android
```

(`render-icons.mjs` needs Playwright's Chromium to draw the SVG.)

## Not done yet

- **Push notifications** that reach a phone whose app is closed (see Notifications above).
- **Sending reports to the server or into the Windows Incident Report Manager.**
- **An app icon for the suite**: the icon is still Field Map's.
- The background sharing and notifications have been tested in a desktop browser standing in for the phone's
  services; how long each phone model really keeps the app running in the background needs checking on devices.
- **Imagery offline**: the same as the web version, online only.
- The installed apps have been tested in a desktop browser standing in for the phone and compiled by CI; they have
  not yet been run on real phones.

## Third-party parts

| Part | Licence | Works offline |
|---|---|---|
| Capacitor 8 (core, android, ios, app, share, local-notifications) | MIT | Yes |
| @capacitor-community/keep-awake | MIT | Yes |
| @capacitor-community/background-geolocation | MIT | Yes |
| MapLibre GL JS (already in the web version) | BSD-3-Clause | Yes |
