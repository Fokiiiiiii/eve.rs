using System.Data;
using System.Text;
using System.Text.Json;
using Eve.Characters;
using Eve.Data;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Corporations;

/// <summary>Corporations and alliances own their schema. A membership change and its fee share one PostgreSQL transaction.</summary>
public sealed record CorporationShareholding(long ShareholderId,long CorporationId,long Shares);
