---
type: "Project"
title: "O-15 (Medium, open): Docker upgrade needs chown to UID 1654"
description: "The Admiral container now runs as UID 1654, so bind-mounted db and logs directories created by root in older installs must be chowned (sudo chown -R 1654:1654 ...). Owner: W5 / operations."
timestamp: "2026-10-05T05:37:06.6411580Z"
created: "2026-10-05T05:37:06.4223660Z"
slug: "open-o15-docker-uid-upgrade"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["docker-images-and-nonroot", "dependency-scanning-control"]
version: "1"
salience: "0.5"
---
Side effect of F-21 (non-root images): existing Docker installs whose docker/armada/db and docker/armada/logs (and armada.json) were created by root fail with permission errors after upgrading. Fix: `sudo chown -R 1654:1654 docker/armada/db docker/armada/logs docker/armada/armada.json`; do not make them world-writable. Documented in docs/DOCKER.md "Non-root containers and upgrades". Owner: W5 / operations (upgrade notes). Status 2026-10-04: open (documented, not automated).
