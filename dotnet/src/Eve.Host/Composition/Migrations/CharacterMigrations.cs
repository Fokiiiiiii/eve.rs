using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private async Task MigrateCharacterStores()
    {
        CharacterNames.EnsureUnicodeSupport();
        characterSchema = Env("EVE_CHARACTERS_SCHEMA", PgCharacterStore.DefaultSchema);
        inventorySchema = Env("EVE_INVENTORY_SCHEMA", PgItemStore.DefaultSchema);
        skillsSchema = Env("EVE_SKILLS_SCHEMA", PgSkillStore.DefaultSchema);
        walletSchema = Env("EVE_WALLET_SCHEMA", PgWalletStore.DefaultSchema);
        fittingSchema = Env("EVE_FITTING_SCHEMA", PgModuleStateStore.DefaultSchema);
        await PgCharacterStore.MigrateAsync(source, characterSchema);
        await PgItemStore.MigrateAsync(source, inventorySchema);
        industrySchema = Env("EVE_INDUSTRY_SCHEMA", "industry");
        if (!System.Text.RegularExpressions.Regex.IsMatch(industrySchema, "^[a-z_][a-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidOperationException("EVE_INDUSTRY_SCHEMA is not a valid PostgreSQL schema name.");
        await Eve.Data.Migrations.ApplyAsync(source, industrySchema,
        [
            IndustryJobsSchema.Jobs(inventorySchema),
        ]);
        await PgSkillStore.MigrateAsync(source, skillsSchema);
        await PgWalletStore.MigrateAsync(source, walletSchema);
        npcSchema = Env("EVE_NPC_SCHEMA", PgBountyStore.DefaultSchema);
        await PgBountyStore.MigrateAsync(source, npcSchema);

        essSchema = Env("EVE_ESS_SCHEMA", PgEssStore.DefaultSchema);
        await PgEssStore.MigrateAsync(source, essSchema);
        await RegisterEssSystemsAsync();
        await PgModuleStateStore.MigrateAsync(source, fittingSchema);
        spaceSchema = Env("EVE_SPACE_SCHEMA", PgSpaceItemStore.DefaultSchema);
        await PgSpaceItemStore.MigrateAsync(source, spaceSchema);
    }
}
