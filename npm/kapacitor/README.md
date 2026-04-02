# @kurrent/kapacitor

CLI companion for [Kurrent Capacitor](https://github.com/kurrent-io/kapacitor) — records and visualizes Claude Code sessions.

## Install

```bash
npm install -g @kurrent/kapacitor
```

## Setup

```bash
kapacitor setup
```

This walks you through: server URL, authentication, Claude Code plugin installation, and verification.

## Commands

```
kapacitor setup                  Configure server, login, and install plugin
kapacitor status                 Show server, auth, and agent status
kapacitor agent start [-d]       Start agent daemon
kapacitor agent stop             Stop agent daemon
kapacitor update                 Check for updates
kapacitor --version              Show version
kapacitor --help                 Show all commands
```
