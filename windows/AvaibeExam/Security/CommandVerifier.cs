using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AvaibeExam.Core;
using AvaibeExam.Models;
using AvaibeExam.Util;

namespace AvaibeExam.Security;

/// <summary>
/// Proof that a <see cref="CommandAuthorization"/> passed every §10.4 check. Only
/// <see cref="CommandVerifier"/> can construct it (internal constructor), which is what lets
/// <c>ReleaseAuthority.VerifiedCommand</c> enforce "verified or nothing" at the type level.
/// </summary>
public sealed class VerifiedAuthorization
{
    internal VerifiedAuthorization(string type, string keyId, string nonce)
    {
        Type = type;
        KeyId = keyId;
        Nonce = nonce;
    }

    public string Type { get; }
    public string KeyId { get; }
    /// <summary>Kept for the SESSION_END / UNLOCK_COMPLETED telemetry only (the server knows it already).</summary>
    public string Nonce { get; }
}

/// <summary>
/// §10.4 command verification:
///   1. signature: ECDSA P-256 / SHA-256, IEEE P1363 (r‖s, 64 bytes) over
///      "avaibe-cmd-v1\n{type}\n{sessionId}\n{deviceId}\n{nonce}\n{issuedAt}\n{expiresAt}" (exact received strings),
///      against the pinned key (compile-time Constants.PinnedServerPublicKey if set, else the TOFU key from enrollment);
///   2. type equals the command type; sessionId / deviceId equal our own;
///   3. keyId equals the pinned keyId (when one is pinned);
///   4. now (corrected by the last serverTime offset) within [issuedAt − 60 s, expiresAt + 60 s];
///   5. nonce never seen before (persisted, DPAPI-protected nonces.dat, 24 h window, ≤ 2000 entries).
/// Every failure returns null with a short log-safe reason; the caller records COMMAND_REJECTED.
/// Signatures, tokens and codes are never logged.
/// </summary>
public sealed class CommandVerifier
{
    private const string LogCat = "verify";
    private const string Prefix = "avaibe-cmd-v1";
    private const int NonceWindowHours = 24;
    private const int MaxNonces = 2000;

    private readonly object _gate = new object();
    private readonly Dictionary<string, DateTimeOffset> _nonces = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    private string? _pinnedSpki;
    private string? _pinnedKeyId;
    private TimeSpan _serverOffset = TimeSpan.Zero;
    private bool _hasServerOffset;

    public CommandVerifier()
    {
        LoadNonces();
    }

    /// <summary>True when a usable pinned key is configured (compile-time or enrollment).</summary>
    public bool HasPinnedKey
    {
        get { lock (_gate) { return !string.IsNullOrEmpty(EffectiveSpki()); } }
    }

    /// <summary>Server time minus local time, from the last serverTime the server sent.</summary>
    public TimeSpan ServerOffset
    {
        get { lock (_gate) { return _serverOffset; } }
    }

    public DateTimeOffset ServerNow
    {
        get { lock (_gate) { return DateTimeOffset.UtcNow + _serverOffset; } }
    }

    // ---- Configuration ---------------------------------------------------------------

    /// <summary>Pins the enrollment (TOFU) key. The compile-time pin, when set, always wins.</summary>
    public void SetPinnedKey(string? spkiBase64, string? keyId)
    {
        lock (_gate)
        {
            _pinnedSpki = string.IsNullOrWhiteSpace(spkiBase64) ? null : spkiBase64.Trim();
            _pinnedKeyId = string.IsNullOrWhiteSpace(keyId) ? null : keyId.Trim();
        }
    }

    /// <summary>Records the server clock offset from an ISO-8601 serverTime (ignored when unparsable or absurd).</summary>
    public void UpdateServerTime(string? serverTime)
    {
        var parsed = Iso8601.Parse(serverTime);
        if (!parsed.HasValue) return;
        var offset = parsed.Value - DateTimeOffset.UtcNow;
        // A server more than a year off is a broken value, not a clock skew.
        if (Math.Abs(offset.TotalDays) > 366) return;
        lock (_gate)
        {
            _serverOffset = offset;
            _hasServerOffset = true;
        }
    }

    private string? EffectiveSpki()
    {
        if (Constants.PinnedServerPublicKey.Length > 0) return Constants.PinnedServerPublicKey;
        return _pinnedSpki;
    }

    // ---- Key validation (used at enrollment) -----------------------------------------

    /// <summary>True when the string is base64 SPKI DER of an ECDSA P-256 public key.</summary>
    public static bool IsValidP256Spki(string? spkiBase64)
    {
        if (string.IsNullOrWhiteSpace(spkiBase64) || spkiBase64.Length > 2048) return false;
        try
        {
            var der = Convert.FromBase64String(spkiBase64.Trim());
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(der, out var read);
            return read == der.Length && ecdsa.KeySize == 256;
        }
        catch
        {
            return false;
        }
    }

    // ---- Verification ----------------------------------------------------------------

    /// <summary>
    /// Verifies <paramref name="auth"/> for <paramref name="expectedType"/> ("RELEASE" / "TERMINATE").
    /// Returns the proof on success; null (with <paramref name="reason"/>) on any failure.
    /// </summary>
    public VerifiedAuthorization? Verify(CommandAuthorization? auth, string expectedType, string sessionId, string deviceId, out string reason)
    {
        try
        {
            return VerifyCore(auth, expectedType, sessionId, deviceId, out reason);
        }
        catch (Exception ex)
        {
            reason = "exception:" + ex.GetType().Name;
            Log.Warn(LogCat, "Verification threw: " + ex.GetType().Name);
            return null;
        }
    }

    private VerifiedAuthorization? VerifyCore(CommandAuthorization? auth, string expectedType, string sessionId, string deviceId, out string reason)
    {
        if (auth == null) { reason = "missing-authorization"; return null; }
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(deviceId)) { reason = "no-session"; return null; }

        var type = auth.Type ?? string.Empty;
        var nonce = auth.Nonce ?? string.Empty;
        var issuedAtText = auth.IssuedAt ?? string.Empty;
        var expiresAtText = auth.ExpiresAt ?? string.Empty;
        var keyId = auth.KeyId ?? string.Empty;
        var signatureText = auth.Signature ?? string.Empty;

        if (!string.Equals(type, expectedType, StringComparison.Ordinal)) { reason = "type-mismatch"; return null; }
        if (!string.Equals(auth.SessionId, sessionId, StringComparison.Ordinal)) { reason = "session-mismatch"; return null; }
        if (!string.Equals(auth.DeviceId, deviceId, StringComparison.Ordinal)) { reason = "device-mismatch"; return null; }
        if (nonce.Length < 16 || nonce.Length > 128) { reason = "bad-nonce"; return null; }
        if (issuedAtText.Length == 0 || expiresAtText.Length == 0 || issuedAtText.Length > 64 || expiresAtText.Length > 64) { reason = "bad-time"; return null; }
        if (signatureText.Length == 0 || signatureText.Length > 512) { reason = "bad-signature-format"; return null; }

        string? spki;
        string? pinnedKeyId;
        lock (_gate)
        {
            spki = EffectiveSpki();
            pinnedKeyId = _pinnedKeyId;
        }
        if (string.IsNullOrEmpty(spki)) { reason = "no-pinned-key"; return null; }
        if (!string.IsNullOrEmpty(pinnedKeyId) && !string.Equals(keyId, pinnedKeyId, StringComparison.Ordinal)) { reason = "keyid-mismatch"; return null; }

        // Time window (server-corrected clock, ±60 s tolerance).
        var issuedAt = Iso8601.Parse(issuedAtText);
        var expiresAt = Iso8601.Parse(expiresAtText);
        if (!issuedAt.HasValue || !expiresAt.HasValue) { reason = "bad-time"; return null; }
        if (expiresAt.Value <= issuedAt.Value) { reason = "bad-time-window"; return null; }
        if ((expiresAt.Value - issuedAt.Value).TotalHours > 24) { reason = "bad-time-window"; return null; }
        var now = ServerNow;
        var skew = TimeSpan.FromSeconds(Constants.CommandTimeSkewSeconds);
        if (now < issuedAt.Value - skew) { reason = "not-yet-valid"; return null; }
        if (now > expiresAt.Value + skew) { reason = "expired"; return null; }

        // Signature.
        byte[] signature;
        byte[] der;
        try
        {
            signature = Convert.FromBase64String(signatureText);
            der = Convert.FromBase64String(spki);
        }
        catch (FormatException)
        {
            reason = "bad-signature-format";
            return null;
        }
        if (signature.Length != 64) { reason = "bad-signature-length"; return null; }

        var canonical = string.Join("\n", new[] { Prefix, type, auth.SessionId ?? string.Empty, auth.DeviceId ?? string.Empty, nonce, issuedAtText, expiresAtText });
        var data = Encoding.UTF8.GetBytes(canonical);

        bool ok;
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportSubjectPublicKeyInfo(der, out _);
            if (ecdsa.KeySize != 256) { reason = "bad-key"; return null; }
            // Default signature format is DSASignatureFormat.IeeeP1363FixedFieldConcatenation (r‖s).
            ok = ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        if (!ok) { reason = "bad-signature"; return null; }

        // Replay protection (checked last so a forged command cannot burn a nonce).
        lock (_gate)
        {
            if (_nonces.ContainsKey(nonce)) { reason = "nonce-replayed"; return null; }
            _nonces[nonce] = DateTimeOffset.UtcNow;
            PruneNoncesLocked();
            SaveNoncesLocked();
        }

        reason = string.Empty;
        Log.Info(LogCat, "Verified " + type + " command (keyId=" + keyId + ", clockOffset=" + FormatOffset() + ")");
        return new VerifiedAuthorization(type, keyId, nonce);
    }

    private string FormatOffset()
    {
        lock (_gate)
        {
            return _hasServerOffset ? Math.Round(_serverOffset.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s" : "unknown";
        }
    }

    // ---- Nonce persistence -----------------------------------------------------------

    private void LoadNonces()
    {
        try
        {
            var json = DeviceIdentity.UnprotectFromFile(Constants.NonceFile);
            if (json == null) return;
            var file = Json.Deserialize<NonceStoreFile>(json);
            if (file?.Entries == null) return;
            lock (_gate)
            {
                foreach (var e in file.Entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.Nonce)) continue;
                    var t = Iso8601.Parse(e.SeenAt) ?? DateTimeOffset.UtcNow;
                    _nonces[e.Nonce] = t;
                }
                PruneNoncesLocked();
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Nonce store unreadable (starting empty): " + ex.GetType().Name);
        }
    }

    private void PruneNoncesLocked()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-NonceWindowHours);
        var stale = new List<string>();
        foreach (var kv in _nonces)
        {
            if (kv.Value < cutoff) stale.Add(kv.Key);
        }
        foreach (var k in stale) _nonces.Remove(k);
        if (_nonces.Count > MaxNonces)
        {
            // Drop the oldest until within the cap.
            var ordered = new List<KeyValuePair<string, DateTimeOffset>>(_nonces);
            ordered.Sort((a, b) => a.Value.CompareTo(b.Value));
            var excess = _nonces.Count - MaxNonces;
            for (var i = 0; i < excess && i < ordered.Count; i++) _nonces.Remove(ordered[i].Key);
        }
    }

    private void SaveNoncesLocked()
    {
        try
        {
            var file = new NonceStoreFile();
            foreach (var kv in _nonces)
            {
                file.Entries.Add(new NonceEntry { Nonce = kv.Key, SeenAt = Iso8601.Format(kv.Value) });
            }
            DeviceIdentity.ProtectToFile(Constants.NonceFile, Json.Serialize(file));
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat, "Nonce store not saved: " + ex.GetType().Name);
        }
    }
}
