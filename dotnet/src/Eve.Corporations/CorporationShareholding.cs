using System.Data;
using System.Text;
using System.Text.Json;
using Eve.Characters;
using Eve.Data;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Corporations;

/// <summary>A shareholder's number of shares in a corporation.</summary>
public sealed record CorporationShareholding(long ShareholderId,long CorporationId,long Shares);
