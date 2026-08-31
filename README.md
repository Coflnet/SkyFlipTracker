## Auction filters
This projects contains filters used for filtering auctions on https://sky.coflnet.com

Use cases:
* Filtering price history from database
* Checking if auction matches for white/blacklisting
    * notifications
    * flipper
    * sniper  

Filters are string based and consist of a `key` and `value`.
Filters have options that tell you what values are valid for `value`.
There are varios types of filters such as `range` `match` and numberic.  
These types can also be combined.

## Deploying
This project should be deployed within a container. 
### Configuration
There are currently no configuration options.

## Cassandra query verification

When a change adds or modifies a Cassandra query, mapping, schema, partition key, or clustering key, run the opt-in final verification from the project directory:

```sh
scripts/cassandra-smoke.sh
```

The command starts a pinned disposable single-node Cassandra instance, creates only the `smoke.unknown_flips2` application schema, and runs the production `FlipStorageService` insert/query mapping for finder type `0` and a defined nonzero finder type. It proves the inclusive time predicates exclude an out-of-window row and that `LIMIT` truncates an in-window partition. It applies generous readiness, driver-query, and total lifecycle hang timeouts, prints only a bounded failure log tail, and always removes its container, network, and volume. It uses no production endpoint or credential.

This is intentionally excluded from normal Docker builds, default `dotnet test`, and ordinary CI. The ordinary provider-level regression remains deterministic and checks the generated bounded CQL, integer partition bindings, `LIMIT`, and absence of `ALLOW FILTERING`.

The endpoint accepts at most a 24-hour UTC interval and returns at most 50 rows. `finder_unknown` reads one partition, so it loads at most 50 rows. `blocked_or_outsped` reads at most 50 rows from each distinct defined nonzero finder partition before the global merge, so its intermediate maximum is `50 × nonzero partition count`, while its response still contains at most 50 rows. Cursor pagination is not currently required because this endpoint provides a bounded operational sample rather than exhaustive history. A cursor across the partition fan-out would need a defined global continuation contract; add that only when a consumer needs exhaustive traversal rather than speculatively.

PR #153 fixed a recurring `Cassandra.InvalidTypeException`: configuring an enum column as Cassandra `int` does not by itself ensure LINQ binds an enum comparison as an integer. Provider-level statement tests should verify the bound runtime types, and the disposable-server command should be the final check whenever Cassandra contracts change.
