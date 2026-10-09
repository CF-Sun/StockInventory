using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StockInventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQuoteIntraday : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QuoteIntraday",
                columns: table => new
                {
                    Symbol = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    BucketUtc = table.Column<DateTime>(type: "datetime2(0)", nullable: false),
                    TradeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(12,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteIntraday", x => new { x.Symbol, x.BucketUtc });
                    table.CheckConstraint("CK_QuoteIntraday_Price", "[Price] > 0");
                    table.ForeignKey(
                        name: "FK_QuoteIntraday_Instruments",
                        column: x => x.Symbol,
                        principalTable: "Instruments",
                        principalColumn: "Symbol");
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteIntraday_TradeDate",
                table: "QuoteIntraday",
                column: "TradeDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuoteIntraday");
        }
    }
}
