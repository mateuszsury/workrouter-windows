# Weryfikacja instalacji

Ta procedura rozdziela testy automatyczne od bramek fizycznych. Wynik `NOT_RUN` pozostaje otwarty i nie powinien być opisywany jako akceptacja produkcyjna.

## Preflight

1. Windows 11 z aktualizacjami, uprawnienia administratora, działający Ethernet i zgodny adapter Wi‑Fi.
2. Zatwierdzony katalog firmowy oraz zgoda właściciela polityki bezpieczeństwa.
3. Brak sekretów w środowisku powłoki i kopiach pakietu.

## Pakiet i instalacja

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-release.ps1 `
  -PackagePath .\artifacts\publish `
  -ValidateScripts
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Launch
```

Weryfikator sprawdza składnię manifestu, każdą sumę SHA-256, kompletność pakietu, wyjście ścieżką poza pakiet oraz obecność wymaganych plików wykonywalnych. Instalator wymaga podwyższenia uprawnień, rejestruje usługę WorkRouter i zapisuje stan w chronionym katalogu systemowym.

## Bramki usługi

- Panel otwiera się lokalnie po uzyskaniu biletu sesji.
- Start routera raportuje potwierdzone pasmo, stan WFP, SMB, uplink i bramę WORK.
- Stop zatrzymuje forwarding, reguły i monitoring w kontrolowanej kolejności.
- Zmiana ustawień pasma zwraca potwierdzenie aktywnego pasma albo błąd z informacją o rollbacku.

## Test klienta

Połącz testowego klienta wyłącznie z SSID WORK. Uruchom z katalogu repozytorium:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\validate-client.ps1 `
  -WorkGateway <adres-bramy-WORK> `
  -HomeTargets <adres-routera>,<adres-NAS>,<adres-usługi>
```

Oczekiwane bramki:

- Internet HTTPS: działa.
- SMB do bramy WORK: działa tylko dla konta udziału.
- Router domowy, NAS i wskazane usługi: zablokowane.
- Trasa do domowego LAN-u i IPv6: zgodne z polityką izolacji.

Powtórz test przed i po uruchomieniu firmowego VPN/EDR. Jeżeli polityka firmy blokuje lokalny SMB, nie próbuj jej obchodzić; oznacz wynik i eskaluj do administratora.

## Testy negatywne i odinstalowanie

Spróbuj połączeń do kilku portów hosta innych niż SMB oraz do co najmniej dwóch urządzeń domowych. Zapisz tylko zanonimizowane wyniki. Po odinstalowaniu sprawdź usunięcie usługi, reguł i konta technicznego; zawartość katalogu firmowego zachowaj lub usuń zgodnie z decyzją właściciela danych.
