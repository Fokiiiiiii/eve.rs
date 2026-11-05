using System.Data;
using System.Text;
using System.Text.Json;
using Eve.Characters;
using Eve.Data;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Corporations;

public sealed record CorporationMemberGrant(int Property,decimal Amount);
