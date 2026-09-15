using System;
using AvaibeExam.Security;

namespace AvaibeExam.Lockdown;

/// <summary>
/// The ONLY three things that may end lockdown (CONTRACT §10.4 / §10.6):
///   * a command whose signature CommandVerifier accepted (RELEASE from a heartbeat or from /unlock),
///   * the offline-grace failsafe,
///   * backing out of a start that failed before the exam page was shown.
/// The constructor is private and <see cref="VerifiedAuthorization"/> can only be created by
/// CommandVerifier, so no code path can release with an unverified or missing authorization
/// (W-02 / W-03). TERMINATE is deliberately NOT a release authority.
/// </summary>
public sealed class ReleaseAuthority
{
    public enum Kind
    {
        VerifiedCommand,
        OfflineGrace,
        StartBackout,
    }

    private ReleaseAuthority(Kind type, string detail, VerifiedAuthorization? authorization)
    {
        Type = type;
        Detail = detail;
        Authorization = authorization;
    }

    public Kind Type { get; }
    /// <summary>Short, log-safe description ("heartbeat", "release-code", "offline 612s", "webview2 failed").</summary>
    public string Detail { get; }
    public VerifiedAuthorization? Authorization { get; }

    public static ReleaseAuthority VerifiedCommand(VerifiedAuthorization authorization, string source)
    {
        if (authorization == null) throw new ArgumentNullException(nameof(authorization));
        return new ReleaseAuthority(Kind.VerifiedCommand, source ?? "command", authorization);
    }

    public static ReleaseAuthority OfflineGrace(int offlineSeconds) =>
        new ReleaseAuthority(Kind.OfflineGrace, "offline " + Math.Max(0, offlineSeconds) + "s", null);

    public static ReleaseAuthority StartBackout(string reason) =>
        new ReleaseAuthority(Kind.StartBackout, reason ?? "start failed", null);

    public string Wire()
    {
        switch (Type)
        {
            case Kind.VerifiedCommand: return "verified-command";
            case Kind.OfflineGrace: return "offline-grace";
            default: return "start-backout";
        }
    }

    public override string ToString() => Wire() + " (" + Detail + ")";
}
