                        .Select(method => $"{serviceName}.{method}");
                    return candidates;
                })
                .Order(StringComparer.Ordinal)
                .ToArray();
            var missingServices = host.Registry.ServiceNames.Distinct(StringComparer.Ordinal)
                .Where(serviceName => rows.All(row => !row.StartsWith($"{serviceName}.", StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.True(missingServices.Length == 0, $"RPC methods were not enumerated for: {string.Join(", ", missingServices)}");
            Assert.Equal(1414, rows.Length);
            Assert.Contains("eventLog.LogClientEvent", rows);
            Console.WriteLine($"RPC_INVENTORY_COUNT={rows.Length}");
            foreach (var row in rows) Console.WriteLine($"RPC_INVENTORY={row}");
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }
