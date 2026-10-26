# Import an existing checkout

Global administrators can register an existing local Git checkout as a vessel.
The Dashboard Vessels page, REST API and MCP tools use the same import service.
The service checks the operator's current administrator role before it reads the
filesystem. Mission credentials and saved Ask credentials cannot use import.

## Configure import folders

Set the parent folders in the Admiral's settings file:

```json
{
  "vesselImport": {
    "allowedRoots": ["/srv/checkouts"]
  }
}
```

The default list is empty. Each root is a parent folder whose direct children
can contain checkouts. Discovery does not search recursively. The root and the
checkout must be real directories with no symbolic link or reparse point in
their path. Each candidate must be the exact top level of a non-bare Git
working tree. Import validates these conditions again when it creates a vessel.
Settings-file reload updates the live allowed-root list.

Discovery returns at most 200 candidates. Its response identifies missing
configuration, root errors and truncation. A candidate that is already
registered includes its existing vessel ID. Import refuses a duplicate path,
including a path already stored as a vessel's working directory or bare path.

## Register a checkout

Open **Import checkout** on the Vessels page. Select a candidate, review its
name and branch, and select an optional fleet. Import validates the vessel name
and the caller's access to the fleet before it creates the record.

The vessel uses the verified local checkout path as its repository source and
working directory. Its managed bare path starts empty. Git origin metadata is
displayed only when it is safe to show without credentials. Registration uses
bounded local metadata reads and leaves checkout files and Git refs unchanged.
Normal docking, checks and landing rules apply to subsequent missions.

Deleting an imported vessel keeps the external checkout. The deletion path can
remove Armada-managed dock or bare repository data associated with the vessel.

## API and MCP

| Operation | REST | MCP |
| --- | --- | --- |
| Discover configured checkouts | `GET /api/v1/vessels/local-checkouts` | `armada_discover_vessel_checkouts` |
| Import one checkout | `POST /api/v1/vessels/import-checkout` | `armada_import_vessel_checkout` |

The import body contains `checkoutPath`, `name` and optional `fleetId`.
The discovery response contains `configured`, `roots`, `candidates`, `errors`
and `truncated`. Each candidate includes `checkoutPath`, `suggestedName`,
`originUrl`, `defaultBranch` and `existingVesselId`.

Both operations require global administrator operator credentials. A bad path or request
returns 400. A duplicate checkout returns 409. An unavailable server import
service returns an explicit Unavailable result; its MCP names remain listed.
