namespace RNGTick;

/// <summary>
/// The one knob this mod has.
///
/// <para>Read on every roll, so it is a plain static field rather than anything with a
/// property getter to inline through. Writing it mid-session is safe in the sense that
/// nothing tears: each roll reads it once and every mode is a pure function. It is not safe
/// in the sense that <b>every client in a multiplayer session must agree</b>, since the mode
/// changes what the simulation computes. Treat it as a setting chosen before a session, not
/// as something to toggle during one.</para>
/// </summary>
public static class RNGTickConfig
{
    /// <summary>
    /// What gets folded into a roll. Defaults to <see cref="OffsetMode.TickAndCycle"/>.
    ///
    /// <para><see cref="OffsetMode.Tick"/> is the narrower change: add the tick and nothing
    /// else. It fixes every reaction in the game, and for a power-of-two modulus such as
    /// <c>RNG.RollPct</c> -- the most-used roll there is -- it moves the bias to different
    /// positions without shrinking it, for the reason spelled out on <see cref="TickOffset"/>.
    /// The default adds a second term that reaches those too.</para>
    /// </summary>
    public static OffsetMode Mode = Default;

    public const OffsetMode Default = OffsetMode.TickAndCycle;

    public static void Reset() => Mode = Default;
}
