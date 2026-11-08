namespace Eve.Inventory;

/// <summary>
/// How much room an item takes in a hold. An assembled item (a ship, a fitted module) takes the volume of its type; a packaged one (a stack of goods) takes what the
/// client's <c>inventorycommon.const</c> says for its group or type, and the type's own volume where it says nothing. A port, as it is, of
/// <c>getInventoryItemUnitVolume</c> of EVE.js (ADR 0009), which mirrors those tables.
/// </summary>
public static class ItemVolumes
{
    /// <summary>The singleton contract wrap whose stored negative quantity encodes its contents' volume.</summary>
    public const int PlasticWrapTypeId = 3468;

    /// <summary>The volume of a packaged ship or other hull by its group (<c>packagedVolumeOverridesPerGroup</c>).</summary>
    private static readonly Dictionary<int, double> PackagedByGroup = new()
    {
        [25] = 2500, [26] = 10000, [27] = 50000, [28] = 20000, [29] = 500, [30] = 10000000, [31] = 500, [324] = 2500, [358] = 10000, [380] = 20000, [381] = 50000,
        [419] = 15000, [420] = 5000, [463] = 3750, [485] = 1300000, [513] = 1300000, [540] = 15000, [541] = 5000, [543] = 3750, [547] = 1300000, [659] = 1300000,
        [830] = 2500, [831] = 2500, [832] = 10000, [833] = 10000, [834] = 2500, [883] = 1300000, [893] = 2500, [894] = 10000, [898] = 50000, [900] = 50000,
        [902] = 1300000, [906] = 10000, [941] = 500000, [963] = 5000, [1022] = 500, [1201] = 15000, [1202] = 20000, [1283] = 2500, [1305] = 5000, [1527] = 2500,
        [1534] = 5000, [1538] = 1300000, [1972] = 10000, [4594] = 1300000, [4902] = 50000, [5087] = 10000, [5120] = 1300000,
    };

    /// <summary>The same by type (<c>packagedVolumeOverridesPerType</c>), which wins over the group.</summary>
    private static readonly Dictionary<int, double> PackagedByType = new()
    {
        // Pinned from V24.01 inventorycommon.const.packagedVolumeOverridesPerType.
        [3293] = 33, // typeMediumStandardContainer
        [3296] = 65, // typeLargeStandardContainer
        [3297] = 10, // typeSmallStandardContainer
        [3465] = 65, // typeLargeSecureContainer
        [3466] = 33, // typeMediumSecureContainer
        [3467] = 10, // typeSmallSecureContainer
        [3534] = 1000, // typeCONCORDCapitalArmorRepairer
        [3536] = 1000, // typeCONCORDCapitalRemoteArmorRepairer
        [3542] = 1000, // typeCONCORDCapitalShieldBooster
        [3544] = 1000, // typeCONCORDCapitalRemoteShieldBooster
        [3546] = 1000, // typeCONCORDIonSiegeBlaster
        [3550] = 1000, // typeCONCORDDual1000mmRailgun
        [3559] = 1000, // typeCONCORDDualGigaPulseLaser
        [3561] = 1000, // typeCONCORDDualGigaBeamLaser
        [3563] = 1000, // typeCONCORDXLCruiseMissileLauncher
        [3565] = 1000, // typeCONCORDXLTorpedoLauncher
        [3571] = 1000, // typeCONCORDQuad3500mmSiegeArtillery
        [3573] = 1000, // typeCONCORDHexa2500mmRepeatingCannon
        [3575] = 1000, // typeCONCORDCapitalRemoteCapacitorTransmitter
        [3616] = 1000, // typeCapitalRemoteShieldBoosterI
        [3618] = 1000, // typeCapitalRemoteShieldBoosterII
        [4252] = 1000, // typeCapitalTractorBeamII
        [4292] = 1000, // typeSiegeModuleII
        [4294] = 1000, // typeTriageModuleII
        [11019] = 100, // typeBHMegaCargoShip
        [11488] = 150, // typeHugeSecureContainer
        [11489] = 300, // typeGiantSecureContainer
        [12219] = 1000, // typeCapitalRemoteCapacitorTransmitterI
        [12223] = 1000, // typeCapitalRemoteCapacitorTransmitterII
        [17363] = 10, // typeSmallAuditLogSecureContainer
        [17364] = 33, // typeMediumAuditLogSecureContainer
        [17365] = 65, // typeLargeAuditLogSecureContainer
        [17366] = 10000, // typeStationContainer
        [17367] = 50000, // typeStationVault
        [17368] = 100000, // typeStationWarehouse
        [20280] = 1000, // typeSiegeModuleI
        [20444] = 1000, // typeDualGigaPulseLaserI
        [20446] = 1000, // typeDualGigaBeamLaserI
        [20448] = 1000, // typeDual1000mmRailgunI
        [20450] = 1000, // typeIonSiegeBlasterI
        [20452] = 1000, // typeHexa2500mmRepeatingCannonI
        [20454] = 1000, // typeQuad3500mmSiegeArtilleryI
        [20539] = 1000, // typeXLTorpedoLauncherI
        [20701] = 1000, // typeCapitalArmorRepairerI
        [20703] = 1000, // typeCapitalShieldBoosterI
        [23674] = 2000, // typeGjallarhornExplosiveDoomsday
        [24283] = 1000, // typeFighterSupportUnitI
        [24445] = 1200, // typeGiantFreightContainer
        [24550] = 2000, // typeJudgementElectromagneticDoomsday
        [24552] = 2000, // typeOblivionKineticDoomsday
        [24554] = 2000, // typeAuroraOminaeThermalDoomsday
        [24569] = 1000, // typeCapitalRemoteArmorRepairerI
        [24644] = 1000, // typeCapitalTractorBeamI
        [27678] = 2000, // typeECMJammerBurstProjector
        [27934] = 1000, // typeCapitalRemoteHullRepairerI
        [27951] = 1000, // typeTriageModuleI
        [28583] = 1000, // typeIndustrialCoreI
        [32444] = 1000, // typeXLCruiseMissileLauncherI
        [33003] = 2500, // typeEnormousFreightContainer
        [33005] = 500, // typeHugeFreightContainer
        [33007] = 100, // typeLargeFreightContainer
        [33009] = 50, // typeMediumFreightContainer
        [33011] = 10, // typeSmallFreightContainer
        [37288] = 1000, // typeRapidTorpedoLauncherI
        [37289] = 1000, // typeQuad800mmRepeatingCannonI
        [37290] = 1000, // typeQuadMegaPulseLaserI
        [37291] = 1000, // typeTripleNeutronBlasterCannonI
        [37292] = 1000, // typeRapidTorpedoLauncherII
        [37293] = 1000, // typeDreadGuristasRapidTorpedoLauncher
        [37294] = 1000, // typeXLTorpedoLauncherII
        [37295] = 1000, // typeXLCruiseMissileLauncherII
        [37296] = 1000, // typeQuadMegaPulseLaserII
        [37297] = 1000, // typeModulatedCompactQuadMegaPulseLaser
        [37298] = 1000, // typeDualGigaPulseLaserII
        [37299] = 1000, // typeDualGigaBeamLaserII
        [37300] = 1000, // typeTripleNeutronBlasterCannonII
        [37301] = 1000, // typeRegulatedCompactTripleNeutronBlasterCannon
        [37302] = 1000, // typeIonSiegeBlasterII
        [37303] = 1000, // typeDual1000mmRailgunII
        [37304] = 1000, // typeQuad800mmRepeatingCannonII
        [37305] = 1000, // typeCompactCarbineQuad800mmRepeatingCannon
        [37306] = 1000, // typeHexa2500mmRepeatingCannonII
        [37307] = 1000, // typeQuad3500mmSiegeArtilleryII
        [40348] = 1000, // type25000mmSteelPlatesI
        [40349] = 1000, // type25000mmCrystallineCarbonideRestrainedPlates
        [40350] = 1000, // type25000mmRolledTungstenCompactPlates
        [40351] = 1000, // type25000mmSteelPlatesII
        [40354] = 1000, // typeCapitalShieldExtenderI
        [40355] = 1000, // typeCapitalAzeotropicRestrainedShieldExtender
        [40356] = 1000, // typeCapitalFS9RegolithCompactShieldExtender
        [40357] = 1000, // typeCapitalShieldExtenderII
        [40631] = 2000, // typeHolyDestinyElectromagneticLance
        [40632] = 2000, // typeDivineHarvestElectromagneticReaper
        [40633] = 2000, // typeBosonicFieldGenerator
        [40634] = 2000, // typeGravitationalTransportationFieldOscillator
        [40635] = 2000, // typeStasisWebificationBurstProjector
        [40636] = 2000, // typeEnergyNeutralizationBurstProjector
        [40659] = 1000, // typeCapitalEnergyNeutralizerI
        [40660] = 1000, // typeCapitalGremlinCompactEnergyNeutralizer
        [40661] = 1000, // typeCapitalInfectiousScopedEnergyNeutralizer
        [40662] = 1000, // typeCapitalEnergyNeutralizerII
        [40663] = 1000, // typeTrueSanshaCapitalEnergyNeutralizer
        [40664] = 1000, // typeDarkBloodCapitalEnergyNeutralizer
        [40665] = 1000, // typeCapitalEnergyNosferatuI
        [40666] = 1000, // typeCapitalGhoulCompactEnergyNosferatu
        [40667] = 1000, // typeCapitalKnaveScopedEnergyNosferatu
        [40668] = 1000, // typeCapitalEnergyNosferatuII
        [40669] = 1000, // typeTrueSanshaCapitalEnergyNosferatu
        [40670] = 1000, // typeDarkBloodCapitalEnergyNosferatu
        [40696] = 2000, // typeWarpDisruptionBurstProjector
        [40697] = 2000, // typeSensorDampeningBurstProjector
        [40698] = 2000, // typeTargetIlluminationBurstProjector
        [40699] = 2000, // typeWeaponDisruptionBurstProjector
        [40714] = 2000, // typeCapitalEmergencyHullEnergizerI
        [40715] = 2000, // typeCapitalImplacableCompactEmergencyHullEnergizer
        [40716] = 2000, // typeCapitalIndefatigableEnduringEmergencyHullEnergizer
        [40717] = 2000, // typeCapitalEmergencyHullEnergizerII
        [40718] = 2000, // typeSistersCapitalEmergencyHullEnergizer
        [41063] = 1000, // typeModalEnduringQuadMegaPulseLaser
        [41064] = 1000, // typeAnodeScopedQuadMegaPulseLaser
        [41065] = 1000, // typeAfocalPreciseQuadMegaPulseLaser
        [41066] = 1000, // typeDarkBloodQuadMegaPulseLaser
        [41067] = 1000, // typeTrueSanshaQuadMegaPulseLaser
        [41076] = 1000, // typeModalEnduringTripleNeutronBlasterCannon
        [41077] = 1000, // typeAnodeScopedTripleNeutronBlasterCannon
        [41078] = 1000, // typeLimitedPreciseTripleNeutronBlasterCannon
        [41079] = 1000, // typeShadowSerpentisTripleNeutronBlasterCannon
        [41080] = 1000, // typeAmpleGalliumQuad800mmRepeatingCannon
        [41081] = 1000, // typeScoutScopedQuad800mmRepeatingCannon
        [41082] = 1000, // typePrototypePreciseQuad800mmRepeatingCannon
        [41083] = 1000, // typeDominationQuad800mmRepeatingCannon
        [41099] = 1000, // typeModulatedCompactDualGigaPulseLaser
        [41100] = 1000, // typeModalEnduringDualGigaPulseLaser
        [41101] = 1000, // typeAnodeScopedDualGigaPulseLaser
        [41102] = 1000, // typeAfocalPreciseDualGigaPulseLaser
        [41103] = 1000, // typeDarkBloodDualGigaPulseLaser
        [41104] = 1000, // typeTrueSanshaDualGigaPulseLaser
        [41114] = 1000, // typeModulatedCompactDualGigaBeamLaser
        [41115] = 1000, // typeModalEnduringDualGigaBeamLaser
        [41116] = 1000, // typeAnodeScopedDualGigaBeamLaser
        [41117] = 1000, // typeAfocalPreciseDualGigaBeamLaser
        [41118] = 1000, // typeDarkBloodDualGigaBeamLaser
        [41119] = 1000, // typeTrueSanshaDualGigaBeamLaser
        [41126] = 1000, // typeRegulatedCompactIonSiegeBlaster
        [41127] = 1000, // typeModalEnduringIonSiegeBlaster
        [41128] = 1000, // typeAnodeScopedIonSiegeBlaster
        [41129] = 1000, // typeLimitedPreciseIonSiegeBlaster
        [41130] = 1000, // typeShadowSerpentisIonSiegeBlaster
        [41138] = 1000, // typeCarbideCompactDual1000mmRailgun
        [41139] = 1000, // typeCompressedEnduringDual1000mmRailgun
        [41140] = 1000, // typeScoutScopedDual1000mmRailgun
        [41141] = 1000, // typePrototypePreciseDual1000mmRailgun
        [41142] = 1000, // typeShadowSerpentisDual1000mmRailgun
        [41150] = 1000, // typeCarbineCompactHexa2500mmRepeatingCannon
        [41151] = 1000, // typeGalliumAmpleHexa2500mmRepeatingCannon
        [41152] = 1000, // typeScoutScopedHexa2500mmRepeatingCannon
        [41153] = 1000, // typePrototypePreciseHexa2500mmRepeatingCannon
        [41154] = 1000, // typeDominationHexa2500mmRepeatingCannon
        [41156] = 1000, // typeCarbideCompactQuad3500mmSiegeArtillery
        [41157] = 1000, // typeGalliumAmpleQuad3500mmSiegeArtillery
        [41158] = 1000, // typeScoutScopedQuad3500mmSiegeArtillery
        [41159] = 1000, // typePrototypePreciseQuad3500mmSiegeArtillery
        [41160] = 1000, // typeDominationQuad3500mmSiegeArtillery
        [41174] = 1000, // typeDreadGuristasXLCruiseMissileLauncher
        [41175] = 1000, // typeArbalestCompactXLCruiseMissileLauncher
        [41176] = 1000, // typeTE2100AmpleXLCruiseMissileLauncher
        [41180] = 1000, // typeArbalestCompactXLTorpedoLauncher
        [41181] = 1000, // typeTE2100AmpleXLTorpedoLauncher
        [41182] = 1000, // typeDreadGuristasXLTorpedoLauncher
        [41223] = 1000, // typeArbalestCompactRapidTorpedoLauncher
        [41224] = 1000, // typeTE2100AmpleRapidTorpedoLauncher
        [41236] = 1000, // type10000MNAfterburnerI
        [41237] = 1000, // type10000MNYS8CompactAfterburner
        [41238] = 1000, // type10000MNMonopropellantEnduringAfterburner
        [41239] = 1000, // type10000MNAfterburnerII
        [41240] = 1000, // typeDomination10000MNAfterburner
        [41241] = 1000, // typeShadowSerpentis10000MNAfterburner
        [41249] = 1000, // type50000MNMicrowarpdriveI
        [41250] = 1000, // type50000MNYT8CompactMicrowarpdrive
        [41251] = 1000, // type50000MNQuadLiFRestrainedMicrowarpdrive
        [41252] = 1000, // type50000MNColdGasEnduringMicrowarpdrive
        [41253] = 1000, // type50000MNMicrowarpdriveII
        [41254] = 1000, // typeDomination50000MNMicrowarpdrive
        [41255] = 1000, // typeShadowSerpentis50000MNMicrowarpdrive
        [41411] = 1000, // typeNetworkedSensorArray
        [41414] = 1000, // typeHermesCompactFighterSupportUnit
        [41415] = 1000, // typeFighterSupportUnitII
        [41417] = 1000, // typeSentientFighterSupportUnit
        [41439] = 2000, // typeIronPikeKineticLance
        [41440] = 2000, // typePhalaricaThermalLance
        [41441] = 2000, // typeGeiravorExplosiveLance
        [41442] = 2000, // typeColdWindKineticReaper
        [41443] = 2000, // typeGuillotineThermalReaper
        [41444] = 2000, // typeJormungandrExplosiveReaper
        [41456] = 1000, // typeCONCORD25000mmSteelPlates
        [41457] = 1000, // typeDarkBlood25000mmSteelPlates
        [41458] = 1000, // typeShadowSerpentis25000mmSteelPlates
        [41459] = 1000, // typeCONCORDCapitalShieldExtender
        [41460] = 1000, // typeTrueSanshaCapitalShieldExtender
        [41461] = 1000, // typeDreadGuristasCapitalShieldExtender
        [41462] = 1000, // typeDominationCapitalShieldExtender
        [41463] = 1000, // typeCapitalIaxEnduringRemoteArmorRepairer
        [41464] = 1000, // typeCapitalCoaxialCompactRemoteArmorRepairer
        [41465] = 1000, // typeCapitalSolaceScopedRemoteArmorRepairer
        [41466] = 1000, // typeCapitalRemoteArmorRepairerII
        [41467] = 1000, // typeDarkBloodCapitalRemoteArmorRepairer
        [41468] = 1000, // typeShadowSerpentisCapitalRemoteArmorRepairer
        [41469] = 1000, // typeCapitalAsymmetricEnduringRemoteShieldBooster
        [41470] = 1000, // typeCapitalMurkyCompactRemoteShieldBooster
        [41471] = 1000, // typeCapitalS95aScopedRemoteShieldBooster
        [41472] = 1000, // typeTrueSanshaCapitalRemoteShieldBooster
        [41473] = 1000, // typeDreadGuristasCapitalRemoteShieldBooster
        [41474] = 1000, // typeDominationCapitalRemoteShieldBooster
        [41475] = 1000, // typeCapitalRemoteHullRepairerII
        [41479] = 1000, // typeCapitalAncillaryRemoteArmorRepairer
        [41483] = 1000, // typeCapitalAncillaryRemoteShieldBooster
        [41484] = 1000, // typeCapitalCapBatteryI
        [41485] = 1000, // typeCapitalCompactPbAcidCapBattery
        [41486] = 1000, // typeCapitalCapBatteryII
        [41487] = 1000, // typeDominationCapitalCapBattery
        [41488] = 1000, // typeDarkBloodCapitalCapBattery
        [41491] = 1000, // typeCapitalCapacitorBoosterI
        [41492] = 1000, // typeCapitalFRXCompactCapacitorBooster
        [41493] = 1000, // typeCapitalCapacitorBoosterII
        [41494] = 1000, // typeDarkBloodCapitalCapacitorBooster
        [41495] = 1000, // typeTrueSanshaCapitalCapacitorBooster
        [41498] = 1000, // typeCapitalIaEnduringArmorRepairer
        [41499] = 1000, // typeCapitalACMCompactArmorRepairer
        [41500] = 1000, // typeCapitalArmorRepairerII
        [41501] = 1000, // typeDarkBloodCapitalArmorRepairer
        [41502] = 1000, // typeShadowSerpentisCapitalArmorRepairer
        [41503] = 1000, // typeCapitalAncillaryArmorRepairer
        [41504] = 1000, // typeCapitalAnciliaryShieldBooster
        [41505] = 1000, // typeCapitalC5LCompactShieldBooster
        [41506] = 1000, // typeCapitalClarityWardEnduringShieldBooster
        [41507] = 1000, // typeCapitalShieldBoosterII
        [41508] = 1000, // typeTrueSanshaCapitalShieldBooster
        [41509] = 1000, // typeDreadGuristasCapitalShieldBooster
        [41510] = 1000, // typeDominationCapitalShieldBooster
        [41511] = 1000, // typeCapitalHullRepairerI
        [41512] = 1000, // typeCapitalIbEnduringHullRepairer
        [41513] = 1000, // typeCapitalIEFCompactHullRepairer
        [41514] = 1000, // typeCapitalHullRepairerII
        [41515] = 1000, // typeCapitalFlexArmorHardenerI
        [41516] = 1000, // typeCapitalFlexShieldHardenerI
        [41525] = 1000, // typeCapitalFlexArmorHardenerII
        [41526] = 1000, // typeDarkBloodCapitalFlexArmorHardener
        [41527] = 1000, // typeShadowSerpentisCapitalFlexArmorHardener
        [41528] = 1000, // typeCapitalFlexShieldHardenerII
        [41529] = 1000, // typeTrueSanshaCapitalFlexShieldHardener
        [41530] = 1000, // typeDreadGuristasCapitalFlexShieldHardener
        [41531] = 1000, // typeDominationCapitalFlexShieldHardener
        [41565] = 1000, // typeCapitalRadiativeScopedRemoteCapacitorTransmitter
        [41566] = 1000, // typeCapitalInductiveCompactRemoteCapacitorTransmitter
        [42244] = 50000, // typePorpoise
        [42890] = 1000, // typeIndustrialCoreII
        [81949] = 1000, // typeCapitalConsortiumTractorBeam
        [83465] = 1000, // typeCapitalMicroJumpDrive
        [83485] = 1000, // typeCapitalMicroJumpDriveFieldGenerator
        [88266] = 1000, // typeStrategosSiegeModule
        [88712] = 1000, // typeXarasierCapitalMicroJumpDrive
        [88713] = 1000, // typeXarasierCapitalAncillaryShieldBooster
        [88718] = 1000, // typeXarasierCapitalAncillaryArmorRepairer
        [90475] = 1000, // typeIntegratedSensorArray
    };

    /// <summary>The volume of one unit of a type once it is packaged.</summary>
    public static double Packaged(int typeId, int groupId, double volume) =>
        PackagedByType.TryGetValue(typeId, out var byType) ? byType : PackagedByGroup.TryGetValue(groupId, out var byGroup) ? byGroup : volume;

    /// <summary>The volume of one unit of an item: of its type when it is assembled (a singleton), packaged otherwise.</summary>
    public static double Unit(int typeId, int groupId, bool singleton, double volume) => singleton ? volume : Packaged(typeId, groupId, volume);

    /// <summary>The volume of an inventory item, including a plastic wrap's encoded contents volume.</summary>
    public static double Of(Item item, int groupId, double typeVolume)
    {
        if (item.Singleton && item.TypeId == PlasticWrapTypeId)
        {
            var encodedVolume = -(double)item.Quantity / 100;
            if (encodedVolume <= 0)
            {
                throw new InvalidOperationException("A singleton plastic wrap must have a negative quantity encoding a positive volume.");
            }
            return encodedVolume;
        }
        return Unit(item.TypeId, groupId, item.Singleton, typeVolume) * (item.Singleton ? 1 : Math.Max(0, item.Quantity));
    }
}
