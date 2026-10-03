using Ocelot.Config.Renderers.Enum;
using Ocelot.Services.Translation;

namespace BOCCHI.Common.Config;

/// <summary>Mode to start once a standalone Treasure Hunt or Carrot Hunt finishes on its own.</summary>
public enum HuntEndStartMode
{
    None = 0,

    IllegalMode = 1,

    MobFarmer = 2,
}

public sealed class HuntEndStartModeDisplay(ITranslator translator) : IEnumDisplay<HuntEndStartMode>
{
    public string Display(HuntEndStartMode value)
    {
        (string key, string fallback) = value switch
        {
            HuntEndStartMode.IllegalMode => ("illegal_mode", "Illegal Mode"),
            HuntEndStartMode.MobFarmer => ("mob_farmer", "Mob Farmer"),
            _ => ("none", "None"),
        };

        string fullKey = $"config.treasure.fields.start_mode_after_hunt.options.{key}";
        return translator.Has(fullKey) ? translator.T(fullKey) : fallback;
    }
}
