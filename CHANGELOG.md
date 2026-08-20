# Changelog

Wszystkie istotne zmiany projektu będą dokumentowane w tym pliku. Projekt stosuje
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.1] - 2026-08-20

### Fixed

- poprawiono przenośne tworzenie katalogów pakietu w PowerShell;
- ponawianie wydania wymaga teraz kontekstu dokładnie tego samego podpisanego taga,
  dzięki czemu tag, źródła i atestacja pochodzenia wskazują jeden commit.

## [0.1.0] - 2026-08-20

### Added

- lokalna usługa Windows zarządzająca hotspotem Mobile Hotspot;
- fail-closed izolacja IPv4 i IPv6 oparta na Windows Filtering Platform;
- udział SMB z dedykowanym kontem i hasłem synchronizowanym z Wi-Fi;
- panel operatorski z obsługą pasm 2,4 GHz i 5 GHz;
- lokalna, ulotna telemetria metadanych DNS i połączeń;
- instalator, deinstalator, launcher oraz walidacja klienta;
- testy jednostkowe i automatyczna walidacja CI.
- bezpieczne samonaprawianie po utracie hotspotu lub filtrów WFP, z zatrzymaniem WORK przed odbudową;
- podpisane tagi wydań, manifesty SHA-256, SPDX SBOM i attestacje GitHub/Sigstore;
- CodeQL, przegląd zależności, OpenSSF Scorecard, Dependabot i publiczna dokumentacja bezpieczeństwa.
