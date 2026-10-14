---
type: "Reference"
title: "Self-registration (POST /api/v1/onboarding) is off by default"
description: "allowSelfRegistration defaults to false (F-10) so nobody can create a user in a known tenant id such as \"default\"; onboarding and tenant lookup are rate limited."
timestamp: "2026-10-05T05:34:37.0358170Z"
created: "2026-10-05T05:34:36.7836910Z"
slug: "self-registration-off"
category: "cat_muusxza5_YzPgstGgnd5CojTlWvb"
links: ["login-rate-limiting", "ask-captain-auto-approve-off"]
version: "1"
salience: "0.5"
---
ArmadaSettings.AllowSelfRegistration default false (new installs and the Docker configs). Before W1 it was on, so anyone who could reach the Admiral could create a user in any tenant whose id they knew (the "default" tenant id is well known) and then use every Authenticated route, including Ask threads that ran captains on the host (F-10, tied to O-02). POST /api/v1/onboarding and POST /api/v1/tenants/lookup are NoAuthRequired routes but counted by the login rate limiter per client address. Enable self-registration only on a trusted network and with Ask auto-approve off. Note: docs/DOCKER.md's sample armada.json still shows "allowSelfRegistration": true in an example block; the shipped default is false.
