# Współpraca przy WorkRouter for Windows

Dziękujemy za poprawki. WorkRouter zmienia ustawienia sieci, WFP, Mobile Hotspot i SMB, dlatego każda zmiana powinna być mała, odwracalna i poparta testem. Nie wykonuj zmian na fizycznym komputerze użytkownika bez jego zgody.

## Przygotowanie

1. Użyj Windows 11 i .NET 8 SDK.
2. Sklonuj repozytorium `workrouter-windows` i utwórz osobną gałąź.
3. Nie kopiuj do repozytorium haseł, tokenów, nazw użytkowników, nazw hostów, adresów sieciowych, plików firmowych, zrzutów ekranu ani capture’ów.

## Zakres zmian

- Kod core powinien zachowywać fail‑closed: błąd aktywacji nie może pozostawić pozornie działającego, niechronionego routera.
- Zmiany WFP, hotspotu, SMB i instalatora muszą mieć test negatywny lub opis pozostałej bramki sprzętowej.
- Panel może pokazywać wyłącznie dane zwracane przez API; nie dodawaj atrap funkcji, których backend nie obsługuje.
- Telemetria nie może obiecywać MITM, payloadu, pełnych URL-i ani pełnej widoczności przez ICS, DoH, ECH, DoT, QUIC lub VPN.
- Nie zmieniaj domyślnych zabezpieczeń tylko po to, aby test był prostszy.

## Walidacja lokalna

Przed zgłoszeniem pull requestu uruchom:

```powershell
dotnet test .\WorkRouter.sln -c Release
node --check .\src\WorkRouter.Service\wwwroot\app.js
```

Jeśli zmiana dotyczy pakietowania, uruchom także:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

Przy zmianie panelu sprawdź ręcznie widok desktopowy i szerokość około 390 px. Przy zmianie API uruchom usługę lokalnie z syntetycznymi danymi i potwierdź odpowiedzi sukcesu, błędu, offline oraz brak autoryzacji. Nie używaj prawdziwego hasła ani tokenu.

Testy jednostkowe i mocki nie zastępują testu z fizycznym laptopem. Zmiany dotyczące sterownika Wi‑Fi, ICS, WFP, SMB, IPv6 albo VPN-a wymagają osobnego opisu testu sprzętowego i jawnego oznaczenia bramki jako nieuruchomionej, jeśli nie była sprawdzona.

## Pull request

Opis powinien zawierać:

- cel i zakres zmiany;
- zmienione komponenty;
- komendy walidacyjne i ich wynik;
- wpływ na bezpieczeństwo, dane i kompatybilność;
- pozostałe `NOT_RUN`, ograniczenia lub wymagania sprzętowe.

Nie dołączaj sekretów do opisu ani artefaktów. Zrzuty UI muszą być syntetyczne i nie mogą zawierać rzeczywistych danych runtime.

## Dokumentacja

Zmiany zachowania, API, konfiguracji, instalacji i odinstalowania muszą aktualizować `README.md`. Nowe zasady ujawniania podatności aktualizuj w `SECURITY.md`. Dokumentacja powinna pozostać po polsku i używać przykładów, które nie ujawniają rzeczywistej sieci użytkownika.

## Licencja

Wnosząc zmianę, zgadzasz się na jej udostępnienie na warunkach licencji MIT znajdującej się w pliku [LICENSE](LICENSE).
