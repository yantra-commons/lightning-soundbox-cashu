using System;
using BTCPayServer.Plugins.SoundboxCashu.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BTCPayServer.Plugins.SoundboxCashu.Migrations;

/// <summary>
/// Hand-written (EF's scaffolder can't be referenced -- see the csproj).
/// Add a new migration class for any later change; never edit this one.
/// </summary>
[DbContext(typeof(SoundboxCashuDbContext))]
[Migration("20261118000000_Initial")]
public class Initial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        const string schema = SoundboxCashuConstants.SchemaName;
        migrationBuilder.EnsureSchema(schema);

        migrationBuilder.CreateTable(
            name: "Settings",
            schema: schema,
            columns: table => new
            {
                StoreId = table.Column<string>(maxLength: 50, nullable: false),
                Enabled = table.Column<bool>(nullable: false),
                MintUrl = table.Column<string>(maxLength: 300, nullable: false),
                ServerUrl = table.Column<string>(maxLength: 300, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Settings", x => x.StoreId));

        migrationBuilder.CreateTable(
            name: "Operations",
            schema: schema,
            columns: table => new
            {
                Id = table.Column<string>(maxLength: 40, nullable: false),
                StoreId = table.Column<string>(maxLength: 50, nullable: false),
                Kind = table.Column<int>(nullable: false),
                State = table.Column<int>(nullable: false),
                MintUrl = table.Column<string>(maxLength: 300, nullable: false),
                Amount = table.Column<long>(nullable: false),
                ReceiveFee = table.Column<long>(nullable: false),
                SettledAmount = table.Column<long>(nullable: true),
                InvoiceId = table.Column<string>(maxLength: 100, nullable: true),
                MeltQuoteId = table.Column<string>(maxLength: 100, nullable: true),
                Token = table.Column<string>(nullable: true),
                LastError = table.Column<string>(maxLength: 500, nullable: true),
                Attempts = table.Column<int>(nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Operations", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Proofs",
            schema: schema,
            columns: table => new
            {
                Id = table.Column<long>(nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                StoreId = table.Column<string>(maxLength: 50, nullable: false),
                MintUrl = table.Column<string>(maxLength: 300, nullable: false),
                KeysetId = table.Column<string>(maxLength: 70, nullable: false),
                Amount = table.Column<long>(nullable: false),
                Secret = table.Column<string>(maxLength: 64, nullable: false),
                BlindingFactor = table.Column<string>(maxLength: 64, nullable: false),
                BlindedMessage = table.Column<string>(maxLength: 66, nullable: false),
                C = table.Column<string>(maxLength: 66, nullable: true),
                State = table.Column<int>(nullable: false),
                CreatedByOperationId = table.Column<string>(maxLength: 40, nullable: false),
                SpentByOperationId = table.Column<string>(maxLength: 40, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Proofs", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_Operations_StoreId_CreatedAt", schema: schema,
            table: "Operations", columns: ["StoreId", "CreatedAt"]);
        migrationBuilder.CreateIndex(name: "IX_Operations_State", schema: schema,
            table: "Operations", column: "State");
        migrationBuilder.CreateIndex(name: "IX_Proofs_Secret", schema: schema,
            table: "Proofs", column: "Secret", unique: true);
        migrationBuilder.CreateIndex(name: "IX_Proofs_StoreId_State", schema: schema,
            table: "Proofs", columns: ["StoreId", "State"]);
        migrationBuilder.CreateIndex(name: "IX_Proofs_CreatedByOperationId", schema: schema,
            table: "Proofs", column: "CreatedByOperationId");
        migrationBuilder.CreateIndex(name: "IX_Proofs_SpentByOperationId", schema: schema,
            table: "Proofs", column: "SpentByOperationId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        const string schema = SoundboxCashuConstants.SchemaName;
        migrationBuilder.DropTable(name: "Proofs", schema: schema);
        migrationBuilder.DropTable(name: "Operations", schema: schema);
        migrationBuilder.DropTable(name: "Settings", schema: schema);
    }
}
