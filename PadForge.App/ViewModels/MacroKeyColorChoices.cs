namespace PadForge.ViewModels
{
    /// <summary>How a Set Key Colors action lights its keys over its time
    /// (#557). Saved by name. APPEND-ONLY.</summary>
    public enum MacroKeyColorStyle
    {
        /// <summary>The color, whole, until the action ends.</summary>
        Steady = 0,
        /// <summary>The color dimming to off over the action's time.</summary>
        Fade = 1,
        /// <summary>The keys going dark one by one over the action's time,
        /// the last one picked first, so the lit keys shrink toward the
        /// first: a cooldown on 1 to 0 or F1 to F12.</summary>
        Countdown = 2,
    }

    /// <summary>How long a Set Key Colors action holds (#557). Saved by
    /// name. APPEND-ONLY.</summary>
    public enum MacroKeyColorHold
    {
        /// <summary>For <c>KeyColorMs</c> milliseconds. Fade and Countdown
        /// always run for a time.</summary>
        Duration = 0,
        /// <summary>Until the macro's trigger is no longer held.</summary>
        UntilRelease = 1,
        /// <summary>Until a Clear Key Colors action.</summary>
        UntilCleared = 2,
    }

    /// <summary>What the lights a Set Key Colors action leaves out show
    /// while it runs (#557). Saved by name. APPEND-ONLY.</summary>
    public enum MacroKeyColorRest
    {
        /// <summary>Their own colors, as the Lighting tab sets them.</summary>
        Keep = 0,
        /// <summary>Off.</summary>
        Off = 1,
        /// <summary>The action's second color.</summary>
        Color = 2,
    }
}
