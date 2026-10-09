BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009142430_AddQuoteIntraday'
)
BEGIN
    CREATE TABLE [QuoteIntraday] (
        [Symbol] varchar(10) NOT NULL,
        [BucketUtc] datetime2(0) NOT NULL,
        [TradeDate] date NOT NULL,
        [Price] decimal(12,4) NOT NULL,
        CONSTRAINT [PK_QuoteIntraday] PRIMARY KEY ([Symbol], [BucketUtc]),
        CONSTRAINT [CK_QuoteIntraday_Price] CHECK ([Price] > 0),
        CONSTRAINT [FK_QuoteIntraday_Instruments] FOREIGN KEY ([Symbol]) REFERENCES [Instruments] ([Symbol])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009142430_AddQuoteIntraday'
)
BEGIN
    CREATE INDEX [IX_QuoteIntraday_TradeDate] ON [QuoteIntraday] ([TradeDate]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009142430_AddQuoteIntraday'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009142430_AddQuoteIntraday', N'8.0.31');
END;
GO

COMMIT;
GO

