using System.Text;

namespace XamaMaster.Services;

/// <summary>
/// DESIGN NOTE - read this before touching it.
///
/// Xama Master encrypts a video's content key so it's safe to store in the
/// .shamm package before it ever reaches a shop. But X Seller (at the shop)
/// still needs to read that content key later, so it can re-wrap it for
/// whichever phone it's sending to. Xama Master can't be physically present
/// at every shop for every sale, so the content key has to survive the trip
/// from Master to Shop in a form only legitimate Shamm software can open.
///
/// The pragmatic offline answer: every official X Seller build embeds the
/// SAME symmetric "distribution key". Xama Master encrypts each content key
/// with it before shipping the package; X Seller decrypts with its
/// embedded copy. This is NOT phone-level security (that's the separate,
/// per-device wrap/unwrap in crypto-core) - it's shop-level trust: it keeps
/// a `.shamm` file useless if it leaks before reaching a shop (e.g. copied
/// off a USB stick in transit), but a determined person who fully reverse
/// engineers an X Seller binary could in principle extract this key.
///
/// That's an accepted trade-off for an offline system with no server to
/// hand out keys dynamically. If you later want stronger protection here,
/// the upgrade path is per-shop distribution keys (Master keeps a list,
/// issues each shop a different one) - flag it and we'll build that next.
/// </summary>
internal static class DistributionKey
{
    // Placeholder for development/testing only. Before any real-world
    // rollout, replace this with a randomly generated 32-byte key (e.g.
    // via shamm_generate_key) and keep it identical - and secret - across
    // every Xama Master and X Seller build you compile.
    private static readonly byte[] DevKey = SHA256Of("shamm-dev-distribution-key-CHANGE-BEFORE-LAUNCH");

    internal static byte[] Current => DevKey;

    private static byte[] SHA256Of(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(s));
    }
}
