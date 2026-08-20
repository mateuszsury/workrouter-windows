# Wsparcie

WorkRouter for Windows jest narzędziem lokalnym. Zanim poprosisz o pomoc:

1. przeczytaj [README](README.md), [weryfikację instalacji](docs/INSTALLATION-VERIFICATION.md) i [model zagrożeń](docs/THREAT-MODEL.md);
2. sprawdź wpisy usługi, panelu i Windows Event Viewer z czasu problemu;
3. powtórz test po zatrzymaniu firmowego VPN/EDR tylko wtedy, gdy zezwala na to polityka organizacji;
4. oznacz bramki fizyczne jako `NOT_RUN`, jeśli nie masz dostępu do testowego laptopa.

## Co dołączyć do issue

Podaj wersję projektu, wersję Windows, ogólny model adaptera, krok reprodukcji i zanonimizowany komunikat błędu. Nie dołączaj haseł, tokenów, nazw użytkowników/hostów, adresów IP, domen firmowych, plików udziału, pełnych zrzutów sieci ani danych klientów.

Problemy bezpieczeństwa zgłaszaj zgodnie z [SECURITY.md](SECURITY.md), a nie w publicznym issue. Użyj prywatnego kanału GitHub Security Advisories.

## Przydatne kontrole lokalne

```powershell
dotnet test WorkRouter.sln -c Release
node --check .\src\WorkRouter.Service\wwwroot\app.js
```

Testy jednostkowe i parsery nie zastępują fizycznej walidacji Mobile Hotspot, ICS, WFP, SMB, IPv6 ani zgodności z VPN/EDR. WorkRouter nie omija polityki firmowego urządzenia; w razie blokady skontaktuj się z administratorem organizacji.

## Zakres wsparcia

Maintainerzy mogą pomóc w reprodukcji lokalnego builda, konfiguracji usługi i interpretacji kontraktu API. Nie gwarantują działania na każdym adapterze, pełnej widoczności HTTPS, izolacji peer-to-peer ani zgodności z nieznanym EDR.
