using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Promo.Campaigns.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "uses");

            migrationBuilder.CreateTable(
                name: "Campaigns",
                schema: "uses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Channel = table.Column<string>(type: "text", nullable: false),
                    Market = table.Column<string>(type: "text", nullable: false),
                    EndsOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DiscountCents = table.Column<long>(type: "bigint", nullable: false),
                    UsesTotal = table.Column<int>(type: "integer", nullable: false),
                    UsesLeft = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                    table.CheckConstraint("CK_Campaigns_Remaining", "\"UsesLeft\" >= 0 AND \"UsesLeft\" <= \"UsesTotal\"");
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "uses",
                columns: table => new
                {
                    Consumer = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.Consumer, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "uses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Module = table.Column<string>(type: "text", nullable: false),
                    RoutingKey = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UseHolds",
                schema: "uses",
                columns: table => new
                {
                    RedemptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Uses = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UseHolds", x => x.RedemptionId);
                    table.ForeignKey(
                        name: "FK_UseHolds_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "uses",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_Code",
                schema: "uses",
                table: "Campaigns",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_SentAt",
                schema: "uses",
                table: "OutboxMessages",
                column: "SentAt",
                filter: "\"SentAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UseHolds_CampaignId",
                schema: "uses",
                table: "UseHolds",
                column: "CampaignId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "uses");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "uses");

            migrationBuilder.DropTable(
                name: "UseHolds",
                schema: "uses");

            migrationBuilder.DropTable(
                name: "Campaigns",
                schema: "uses");
        }
    }
}
