using System.Data;
using System.Text;
using System.Text.Json;
using Eve.Characters;
using Eve.Data;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Corporations;

/// <summary>A rented corporation office and its current 30-day term.</summary>
public sealed record CorporationOffice(long OfficeId, long OfficeFolderId, long CorporationId, long StationId, long SolarSystemId,
    decimal RentIsk, long RentedAtMs, long ExpiresAtMs);

/// <summary>Static station facts used to seed deterministic office pricing.</summary>
public sealed record CorporationOfficeStation(long Id, int TypeId, long OwnerId, long SolarSystemId,
    double Security, int OfficeCapacity = 24);

/// <summary>Durable corporation office rentals, daily rental quotes, and the next-cycle bill.</summary>
public sealed record CorporationOfficeRentBill(
    long BillId,
    long OfficeId,
    long CorporationId,
    long StationId,
    decimal AmountIsk,
    long IssuedAtMs,
    long DueAtMs,
    int Status,
    long? PaidAtMs = null,
    long? PaidByOwnerId = null);

public sealed record CorporationBillAutomaticPaySettings(
    int DivisionId,
    IReadOnlyDictionary<int, bool> EnabledBillTypes);
