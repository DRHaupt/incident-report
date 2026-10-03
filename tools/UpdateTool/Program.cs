using Shared.Core.Update;

// Release helper for the Field Suite updater (used by CI; also runnable by hand with `dotnet run --project tools/UpdateTool -- …`).
//
//   keygen                          New signing key: prints the private key (put it in the UPDATE_SIGNING_KEY secret) and the public key.
//   public-key                      Prints the public key for the private key in UPDATE_SIGNING_KEY (nothing, if unset).
//   manifest <msi> <version> <dir>  Writes fieldsuite-update.json for the MSI into <dir>; signs it when UPDATE_SIGNING_KEY is set.
//            [--notes <text>]
//   verify <dir> [<keys file>]      Checks the manifest and MSI in <dir> (checksum, and signature against the keys file if given).

const string KeyVariable = "UPDATE_SIGNING_KEY";
var key = Environment.GetEnvironmentVariable(KeyVariable);
if (string.IsNullOrWhiteSpace(key))
    key = null;

try
{
    switch (args.FirstOrDefault())
    {
        case "keygen":
        {
            var (priv, pub) = UpdateSigning.CreateKey();
            Console.WriteLine("Private key (keep secret; save as the GitHub Actions secret " + KeyVariable + "):");
            Console.WriteLine(priv);
            Console.WriteLine();
            Console.WriteLine("Public key (the release build writes it into update-keys.txt automatically):");
            Console.WriteLine(pub);
            return 0;
        }
        case "public-key":
            if (key != null)
                Console.WriteLine(UpdateSigning.PublicKeyOf(key));
            return 0;

        case "manifest" when args.Length >= 4:
        {
            var notesAt = Array.IndexOf(args, "--notes");
            var notes = notesAt > 0 && notesAt + 1 < args.Length ? args[notesAt + 1] : "";
            var m = UpdateManifest.ForFile(args[1], args[2], notes, DateTime.UtcNow);
            if (key != null)
                UpdateSigning.Sign(m, key);
            Directory.CreateDirectory(args[3]);
            var path = Path.Combine(args[3], UpdateManifest.FileName);
            File.WriteAllText(path, m.ToJson());
            Console.WriteLine($"Wrote {path}: {m.File} {m.Version}, {m.Size:N0} bytes, {(key != null ? "signed" : "NOT signed (no " + KeyVariable + ")")}");
            return 0;
        }
        case "verify" when args.Length >= 2:
        {
            var m = UpdateManifest.Parse(File.ReadAllText(Path.Combine(args[1], UpdateManifest.FileName)));
            var msi = Path.Combine(args[1], m.File);
            if (new FileInfo(msi).Length != m.Size || UpdateChecker.HashFile(msi) != m.Sha256.ToLowerInvariant())
                throw new InvalidDataException($"{m.File} doesn't match the manifest checksum.");
            var trust = args.Length >= 3 ? UpdateTrust.Load(args[2]) : new UpdateTrust(Array.Empty<string>());
            var verdict = trust.Check(m);
            Console.WriteLine($"{m.File} {m.Version}: checksum OK, signature {verdict}");
            return verdict == UpdateTrustResult.Rejected ? 1 : 0;
        }
        default:
            Console.Error.WriteLine("Usage: keygen | public-key | manifest <msi> <version> <dir> [--notes <text>] | verify <dir> [<keys file>]");
            return 2;
    }
}
catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}
