using System.Globalization;

namespace Installer.Core.Schema;

/// <summary>
/// Compares two fingerprints and emits findings in the estate's drift-key vocabulary.
///
/// Pure and static: no database, no clock, no configuration. Every classification decision below
/// is therefore testable without provisioning anything, which matters because the decisions are
/// the interesting part — capture is mechanical, classification is judgement.
/// </summary>
internal static class SchemaDriftComparer
{
    public static SchemaDriftReport Compare(
        SchemaFingerprint expected,
        SchemaFingerprint actual,
        IReadOnlyDictionary<string, string>? acknowledged)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var findings = new List<DriftFinding>();

        CompareTablePresence(expected, actual, findings);
        CompareViews(expected, actual, findings);

        foreach (var name in expected.Tables.Keys.Intersect(actual.Tables.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            CompareTable(expected.Tables[name], actual.Tables[name], findings);
        }

        return Acknowledge(findings, acknowledged);
    }

    private static void CompareTablePresence(SchemaFingerprint expected, SchemaFingerprint actual, List<DriftFinding> findings)
    {
        foreach (var t in expected.Tables.Keys.Except(actual.Tables.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // BREAKING. The release expects this table; any statement touching it fails at run
            // time with ERROR 1146, which surfaces as a feature bug days later.
            findings.Add(new DriftFinding
            {
                Key = $"live-missing-table:{t}",
                Severity = DriftSeverity.Breaking,
                Message = $"Table `{t}` is expected by this release but is not in the database."
            });
        }

        foreach (var t in actual.Tables.Keys.Except(expected.Tables.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // BENIGN. Almost always a table from an older release, or one a DBA added. The
            // application does not read it, so it cannot change behaviour — but it is reported,
            // because an unexplained table is how a half-finished migration announces itself.
            findings.Add(new DriftFinding
            {
                Key = $"live-extra-table:{t}",
                Severity = DriftSeverity.Benign,
                Message = $"Table `{t}` is in the database but not expected by this release."
            });
        }
    }

    private static void CompareViews(SchemaFingerprint expected, SchemaFingerprint actual, List<DriftFinding> findings)
    {
        foreach (var v in expected.Views.Except(actual.Views, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            findings.Add(new DriftFinding
            {
                Key = $"live-missing-view:{v}",
                Severity = DriftSeverity.Breaking,
                Message = $"View `{v}` is expected by this release but is not in the database."
            });
        }

        foreach (var v in actual.Views.Except(expected.Views, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            findings.Add(new DriftFinding
            {
                Key = $"live-extra-view:{v}",
                Severity = DriftSeverity.Benign,
                Message = $"View `{v}` is in the database but not expected by this release."
            });
        }
    }

    private static void CompareTable(TableFingerprint expected, TableFingerprint actual, List<DriftFinding> findings)
    {
        var t = expected.Name;

        foreach (var c in expected.Columns.Keys.Except(actual.Columns.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            findings.Add(new DriftFinding
            {
                Key = $"live-missing-column:{t}.{c}",
                Severity = DriftSeverity.Breaking,
                Message = $"Column `{t}`.`{c}` is expected by this release but is not in the database."
            });
        }

        foreach (var c in actual.Columns.Keys.Except(expected.Columns.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var col = actual.Columns[c];

            // An extra column is benign ONLY if the application can ignore it. A NOT NULL column
            // with no default cannot be ignored: every INSERT this release issues omits it, and
            // every one of them fails with ERROR 1364. That is a breaking difference wearing the
            // costume of an additive one, and it is the shape a half-applied migration leaves.
            var insertable = col.IsNullable || col.Default is not null;

            findings.Add(new DriftFinding
            {
                Key = $"live-extra-column:{t}.{c}",
                Severity = insertable ? DriftSeverity.Benign : DriftSeverity.Breaking,
                Message = insertable
                    ? $"Column `{t}`.`{c}` is in the database but not expected by this release."
                    : $"Column `{t}`.`{c}` is in the database, not expected by this release, and is NOT NULL with no " +
                      "default — every INSERT this release issues omits it and will fail with ERROR 1364."
            });
        }

        foreach (var c in expected.Columns.Keys.Intersect(actual.Columns.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            CompareColumn(t, expected.Columns[c], actual.Columns[c], findings);
        }

        ComparePrimaryKey(expected, actual, findings);
        CompareIndexes(expected, actual, findings);
        CompareForeignKeys(expected, actual, findings);
    }

    private static void CompareColumn(string table, ColumnFingerprint expected, ColumnFingerprint actual, List<DriftFinding> findings)
    {
        var c = expected.Name;

        if (!string.Equals(expected.Type, actual.Type, StringComparison.Ordinal))
        {
            // BREAKING, always, and deliberately without trying to decide whether a particular
            // widening is safe. This estate has the scar: a money column that became varchar
            // through a merge tie-break, and Dapper silently rounding a decimal to int because
            // the C# property had not moved with the column. A type difference is exactly where
            // that class of defect lives, and an unattended installer is the wrong place to
            // adjudicate it.
            findings.Add(new DriftFinding
            {
                Key = $"live-shape-type:{table}.{c}",
                Severity = DriftSeverity.Breaking,
                Message = $"Column `{table}`.`{c}` is `{actual.Type}` in the database; this release expects `{expected.Type}`."
            });
        }

        if (expected.IsNullable != actual.IsNullable)
        {
            // A column that is NOT NULL where the release expects nullable rejects writes the
            // release considers valid. Nullable where NOT NULL is expected admits rows the
            // release's invariants assume cannot exist. Both are correctness problems.
            findings.Add(new DriftFinding
            {
                Key = $"live-shape-null:{table}.{c}",
                Severity = DriftSeverity.Breaking,
                Message = $"Column `{table}`.`{c}` is {(actual.IsNullable ? "NULL" : "NOT NULL")} in the database; " +
                          $"this release expects {(expected.IsNullable ? "NULL" : "NOT NULL")}."
            });
        }
        else if (!string.Equals(expected.Default, actual.Default, StringComparison.Ordinal))
        {
            // COMPATIBLE. A different default changes what a row gets when the release does not
            // supply a value — which matters, but does not stop the release running, and a DBA
            // may have set it deliberately. Only checked when nullability agrees: a nullability
            // change already implies a default change and reporting both is noise.
            findings.Add(new DriftFinding
            {
                Key = $"live-shape-default:{table}.{c}",
                Severity = DriftSeverity.Compatible,
                Message = $"Column `{table}`.`{c}` defaults to {Show(actual.Default)} in the database; " +
                          $"this release expects {Show(expected.Default)}."
            });
        }
    }

    private static void ComparePrimaryKey(TableFingerprint expected, TableFingerprint actual, List<DriftFinding> findings)
    {
        if (expected.PrimaryKey.SequenceEqual(actual.PrimaryKey, StringComparer.Ordinal))
        {
            return;
        }

        // BREAKING, including a reordering. Composite key order decides which prefix lookups the
        // index serves, so a reordered key silently turns indexed reads into full scans — the
        // estate found 18 of 47 unwind reads doing exactly that, and they lock the table.
        findings.Add(new DriftFinding
        {
            Key = $"live-shape-pk:{expected.Name}",
            Severity = DriftSeverity.Breaking,
            Message = $"Primary key of `{expected.Name}` is ({Join(actual.PrimaryKey)}) in the database; " +
                      $"this release expects ({Join(expected.PrimaryKey)})."
        });
    }

    private static void CompareIndexes(TableFingerprint expected, TableFingerprint actual, List<DriftFinding> findings)
    {
        var t = expected.Name;

        foreach (var i in expected.Indexes.Keys.Except(actual.Indexes.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // COMPATIBLE, not breaking: queries still return the right answer. But not benign
            // either — the estate measured what a missing index costs here, and it is not
            // latency: an unwind read without its index scans the table FOR UPDATE and locks it
            // for the life of a correction.
            findings.Add(new DriftFinding
            {
                Key = $"live-missing-index:{t}.{i}",
                Severity = DriftSeverity.Compatible,
                Message = $"Index `{i}` on `{t}` is expected by this release but is not in the database. " +
                          "Queries remain correct; reads that relied on it become table scans."
            });
        }

        foreach (var i in actual.Indexes.Keys.Except(expected.Indexes.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            findings.Add(new DriftFinding
            {
                Key = $"live-extra-index:{t}.{i}",
                Severity = DriftSeverity.Benign,
                Message = $"Index `{i}` on `{t}` is in the database but not expected by this release."
            });
        }

        foreach (var i in expected.Indexes.Keys.Intersect(actual.Indexes.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!expected.Indexes[i].SequenceEqual(actual.Indexes[i], StringComparer.Ordinal))
            {
                // Same name, different columns. The estate flags this as its own class because
                // it is the confusing one: everything reports the index as present.
                findings.Add(new DriftFinding
                {
                    Key = $"live-shape-index:{t}.{i}",
                    Severity = DriftSeverity.Compatible,
                    Message = $"Index `{i}` on `{t}` covers ({Join(actual.Indexes[i])}) in the database; " +
                              $"this release expects ({Join(expected.Indexes[i])}). Same name, different index."
                });
            }
        }
    }

    private static void CompareForeignKeys(TableFingerprint expected, TableFingerprint actual, List<DriftFinding> findings)
    {
        var t = expected.Name;

        foreach (var fk in expected.ForeignKeys.Except(actual.ForeignKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // COMPATIBLE. A missing constraint does not break a correct application; it stops
            // the database catching an incorrect one. Worth knowing, not worth blocking — and
            // the estate deliberately removed three constraints that were never valid keys.
            findings.Add(new DriftFinding
            {
                Key = $"live-missing-fk:{t}.{fk}",
                Severity = DriftSeverity.Compatible,
                Message = $"Foreign key `{fk}` on `{t}` is expected by this release but is not in the database."
            });
        }

        foreach (var fk in actual.ForeignKeys.Except(expected.ForeignKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // An UNEXPECTED constraint can reject writes this release considers valid, which is
            // why it outranks a missing one.
            findings.Add(new DriftFinding
            {
                Key = $"live-extra-fk:{t}.{fk}",
                Severity = DriftSeverity.Compatible,
                Message = $"Foreign key `{fk}` on `{t}` is in the database but not expected by this release; " +
                          "it may reject writes this release considers valid."
            });
        }
    }

    private static SchemaDriftReport Acknowledge(List<DriftFinding> findings, IReadOnlyDictionary<string, string>? acknowledged)
    {
        if (acknowledged is null || acknowledged.Count == 0)
        {
            return new SchemaDriftReport { Findings = findings, StaleAcknowledgements = [] };
        }

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = findings
            .Select(f =>
            {
                if (!acknowledged.TryGetValue(f.Key, out var reason))
                {
                    return f;
                }

                matched.Add(f.Key);
                return f with { IsAcknowledged = true, AcknowledgementReason = reason };
            })
            .ToList();

        var stale = acknowledged.Keys
            .Where(k => !matched.Contains(k))
            .Order(StringComparer.Ordinal)
            .ToList();

        return new SchemaDriftReport { Findings = result, StaleAcknowledgements = stale };
    }

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    private static string Show(string? value) =>
        value is null ? "no default" : string.Create(CultureInfo.InvariantCulture, $"'{value}'");
}
