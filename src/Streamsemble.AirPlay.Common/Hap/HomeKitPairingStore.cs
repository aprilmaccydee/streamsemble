using System.Text.Json;

namespace Streamsemble.AirPlay.Common.Hap;

/// <summary>
/// Persistent HomeKit controller identity + the set of accessories we have
/// completed pair-setup with. A verified AirPlay 2 receiver (e.g. a TV) only
/// renders buffered audio for a controller it recognises, so we must keep a
/// stable Ed25519 long-term identity and remember each accessory's long-term
/// public key across process restarts.
///
/// Stored as JSON at <c>STREAMSEMBLE_PAIRING_STORE</c> or, by default,
/// <c>~/.streamsemble/homekit-pairings.json</c>.
/// </summary>
public sealed class HomeKitPairingStore
{
    private sealed class Model
    {
        public string ControllerId { get; set; } = "";
        public string ControllerLtsk { get; set; } = ""; // base64 Ed25519 seed (32)
        public string ControllerLtpk { get; set; } = ""; // base64 Ed25519 public (32)
        public Dictionary<string, Accessory> Accessories { get; set; } = new();
    }

    public sealed class Accessory
    {
        public string AccessoryId { get; set; } = "";
        public string AccessoryLtpk { get; set; } = ""; // base64 Ed25519 public (32)
    }

    private readonly string _path;
    private readonly Model _model;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Our controller pairing identifier (stable UUID string).</summary>
    public string ControllerId => _model.ControllerId;

    /// <summary>Our Ed25519 long-term secret seed (32 bytes).</summary>
    public byte[] ControllerLtsk => Convert.FromBase64String(_model.ControllerLtsk);

    /// <summary>Our Ed25519 long-term public key (32 bytes).</summary>
    public byte[] ControllerLtpk => Convert.FromBase64String(_model.ControllerLtpk);

    private HomeKitPairingStore(string path, Model model)
    {
        _path = path;
        _model = model;
    }

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("STREAMSEMBLE_PAIRING_STORE") is { Length: > 0 } p
            ? p
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".streamsemble", "homekit-pairings.json");

    /// <summary>Load the store, generating a fresh controller identity on first use.</summary>
    public static HomeKitPairingStore Load(string? path = null)
    {
        path ??= DefaultPath;
        Model model;
        if (File.Exists(path))
        {
            model = JsonSerializer.Deserialize<Model>(File.ReadAllText(path)) ?? new Model();
        }
        else
        {
            model = new Model();
        }

        if (string.IsNullOrEmpty(model.ControllerLtsk))
        {
            var (seed, pub) = PairingCrypto.GenerateEd25519();
            model.ControllerId = Guid.NewGuid().ToString();
            model.ControllerLtsk = Convert.ToBase64String(seed);
            model.ControllerLtpk = Convert.ToBase64String(pub);
            var store = new HomeKitPairingStore(path, model);
            store.Save();
            return store;
        }

        return new HomeKitPairingStore(path, model);
    }

    public Accessory? GetAccessory(string deviceKey)
        => _model.Accessories.TryGetValue(deviceKey, out var a) ? a : null;

    public void SaveAccessory(string deviceKey, string accessoryId, byte[] accessoryLtpk)
    {
        _model.Accessories[deviceKey] = new Accessory
        {
            AccessoryId = accessoryId,
            AccessoryLtpk = Convert.ToBase64String(accessoryLtpk),
        };
        Save();
    }

    public void ForgetAccessory(string deviceKey)
    {
        if (_model.Accessories.Remove(deviceKey))
        {
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_model, JsonOptions));
    }
}
