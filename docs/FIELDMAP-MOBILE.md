# Field Map for iOS and Android (installed app)

The same screens as [Field Map for phones](FIELDMAP-WEB.md), packaged as a real app with
[Capacitor](https://capacitorjs.com) 6 (MIT licence). The app's files are inside the app; the map, search, nearest,
directions, team and live data still come from **your Field Map server**. Each phone is told the server's address
once, on first start.

The project is in `mobile/`. There is no second copy of the app's code: `mobile/scripts/copy-web.mjs` copies
`src/FieldMap.Web/wwwroot` into the Android and Xcode projects, so every change to the web version is in the next
app build too.

## What's different from the web version

| | Web version (Safari/Chrome, Home Screen) | Installed app |
|---|---|---|
| Where the app's files are | On the server, kept by the browser | In the app: it starts with no signal |
| Server | The site it was opened from | Asked on first start; change it under More → Server |
| Sign-in (access code) | Cookie | A token the app keeps and sends with each request |
| Map with no signal | Areas viewed or saved (browser storage) | The same, kept in the app's own storage (up to 12,000 tiles) |
| Map info, hazmat (ERG), first aid, event files with no signal | Last copy | Last copy |
| Keep the screen on | Browser wake lock, where supported | Android: always works. iPhone: where iOS supports it (see "Capacitor version") |
| Send my location | Share sheet on iPhone; copy on some Android browsers | Share sheet on both |
| Android Back button | Leaves the page | One step back in the panel, then closes it, then leaves |
| GPS with the app in the background | No | No (not yet: see "Not done yet") |

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

You need Node 22. For Android: Android Studio (or JDK 17 and the Android SDK). For iOS: a Mac with Xcode 15 or newer.

```sh
cd mobile
npm ci
npm run sync        # copies the web app and updates both native projects
npx cap open android    # or: npx cap open ios
```

Then Run in Android Studio or Xcode. From the command line, `cd android && ./gradlew assembleDebug` writes
`app/build/outputs/apk/debug/app-debug.apk`.

**GitHub Actions** (`.github/workflows/mobile.yml`) builds both whenever `mobile/` or the web app changes:

- **Android**: the APK is an artifact of the run (`FieldMap-android-<version>`, kept 14 days). Copy it to a phone
  and open it to install ("Install unknown apps" must be allowed for the app you open it with).
- **iOS**: compiled for the simulator only, to prove the project builds. Apple doesn't allow installing on
  iPhones without signing (below).

## Before giving it to crews

These are decisions and accounts only the organisation can make, so they are not set up:

1. **App ID.** `org.fieldsuite.fieldmap` is a placeholder. Pick your own reverse-domain ID before the first
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
   manifest) and want a privacy policy. The app sends position only to the server address the user entered, and
   only while sharing is turned on.

## Capacitor version

The project is on **Capacitor 6** so it opens in **Xcode 15**. That is enough for the simulator and for running on
your own phone, but Apple's stores want apps built with a current Xcode, so move back up to Capacitor 8 (Xcode 26)
before a TestFlight or App Store release. The Capacitor 8 projects are in the repository history (commit
`d29cf7d`). Moving up means: Capacitor 8 packages in `mobile/package.json`, regenerated `android/` and `ios/`
projects with the same permissions, icons and signing set-up, `@capacitor-community/keep-awake` in place of
`KeepAwakePlugin.java` (which also gives iPhones a reliable keep-screen-on), and Java 21 / `macos-26` in
`.github/workflows/mobile.yml`.

While on Capacitor 6, iOS uses Swift Package Manager (marked experimental in that version; no CocoaPods needed).

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

- **Position sharing with the app in the background or the screen off.** This is the main thing a native app can
  do that the web version can't. It needs a background-location service on Android and the "Always" location
  permission on iOS, and both stores review that closely. Until then More → Keep the screen on is the way.
- **Push notifications** for SOS and shared pins when the app is closed.
- **Imagery offline**: the same as the web version, online only.
- The installed apps have been tested in a desktop browser standing in for the phone and compiled by CI; they have
  not yet been run on real phones.

## Third-party parts

| Part | Licence | Works offline |
|---|---|---|
| Capacitor 6 (core, android, ios, app, share) | MIT | Yes |
| MapLibre GL JS (already in the web version) | BSD-3-Clause | Yes |
