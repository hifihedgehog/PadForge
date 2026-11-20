namespace PadForge.Engine.Steering
{
    /// <summary>
    /// The most torque a wheel gives, in N·m, for the models whose maker
    /// publishes a figure (#588). Vehicle mode's torque is in N·m at the
    /// rim, and this figure turns it into a share of the wheel's motor.
    /// A wheel that is not here starts from
    /// <see cref="CenteringSettings.UnlistedWheelTorque"/>, which makes
    /// its forces weaker, never stronger, until its owner enters the
    /// wheel's own figure.
    ///
    /// <para>Sources, read 2026-11-19. Logitech: its store pages name the
    /// G923 a "2.3 Nm" wheel and its Driving Force page the G29 and G920
    /// a "2.3Nm Racing System". Fanatec: its Wheel Base Torque FAQ
    /// (help.fanatec.com article 48812077676177) gives the CSL DD 5 N·m,
    /// or 8 with the Boost Kit 180, the ClubSport DD up to 15, the Podium
    /// DD1 20 and the Podium DD2 25, and its "Which wheel bases have
    /// limited torque?" page gives the ClubSport V2.5 8. Thrustmaster
    /// publishes no figure for the wheels PadForge drives, so none is
    /// listed.</para>
    ///
    /// <para>One Fanatec id, 0x0020, is the CSL DD, the Gran Turismo DD
    /// Pro and the ClubSport DD alike. It takes the most of the three,
    /// since a figure above the wheel's own only makes the forces
    /// weaker.</para>
    /// </summary>
    public static class WheelTorqueList
    {
        private const ushort Logitech = 0x046D, Fanatec = 0x0EB7;

        /// <summary>The listed figure for a model in N·m, or 0 where the
        /// model is not listed.</summary>
        public static double For(ushort vendorId, ushort productId)
        {
            if (vendorId == Logitech)
            {
                switch (productId)
                {
                    case 0xC24F: // G29 (PS3 / PC)
                    case 0xC260: // G29 (PS4)
                    case 0xC262: // G920
                    case 0xC266: // G923 (PS / PC)
                    case 0xC267: // G923 (PS / PC)
                    case 0xC26E: // G923 (Xbox / PC)
                        return 2.3;
                }
                return 0;
            }
            if (vendorId == Fanatec)
            {
                switch (productId)
                {
                    case 0x0020: return 15.0; // CSL DD (5, or 8 boosted), GT DD Pro, ClubSport DD (15)
                    case 0x0004: return 8.0;  // ClubSport V2.5
                    case 0x0006: return 20.0; // Podium DD1
                    case 0x0007: return 25.0; // Podium DD2
                }
            }
            return 0;
        }
    }
}
