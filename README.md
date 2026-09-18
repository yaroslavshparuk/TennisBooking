# TennisBooking

[![CI](https://github.com/yaroslavshparuk/TennisBooking/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/yaroslavshparuk/TennisBooking/actions/workflows/ci.yml)
![Coverage](https://img.shields.io/badge/line%20coverage-100%25-brightgreen)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![Status](https://img.shields.io/badge/status-personal%20project-blue)

Personal project for making tennis court booking through Skedda less annoying.

It handles the booking routine for my regular court schedule and sends Telegram updates when something important happens, so I do not have to babysit the booking window manually.

## Auth (OpenID Connect)

All routes (pages and the Hangfire dashboard) require sign-in via a single
configurable OIDC provider. There is no other auth (the old Hangfire
username/password is gone).

1. In Pocket ID (or any OIDC issuer), create a client with:
   - callback URL `https://<your-host>/signin-oidc`
   - post-logout redirect `https://<your-host>/signout-callback-oidc`
2. Configure the app (`appsettings.json` or environment variables):

   | Setting | Env var | Example |
   |---|---|---|
   | `Auth:Authority` | `Auth__Authority` | `https://id.example.com` |
   | `Auth:ClientId` | `Auth__ClientId` | `tennis-booking` |
   | `Auth:ClientSecret` | `Auth__ClientSecret` | (secret, env var only) |
   | `Auth:Scopes` | `Auth__Scopes__0…` | `openid`, `profile`, `email` |

   To switch providers later, just point `Auth:Authority`/`ClientId`/`ClientSecret`
at the new issuer — no code changes needed.

Only `/health` and `/health/ready` stay anonymous (container probes).
