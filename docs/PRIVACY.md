# Prywatność i telemetria

## Zasada lokalności

WorkRouter nie wysyła telemetrii do zewnętrznej usługi. Panel i API działają lokalnie. Dane są przeznaczone do diagnostyki operatora tego komputera.

## Jakie dane mogą wystąpić

- Stan routera, interfejsów, WFP, SMB i klientów WORK.
- Agregaty użycia oraz ulotne metadane przepływów: czas, kierunek, protokół, adres/port, rozmiar pakietu, domena/host/SNI, jeśli można je skorelować.
- Heurystyczne alerty i liczniki ograniczonej widoczności.

Nie zapisujemy treści pakietów, payloadów, haseł, pełnych URL-i ani odszyfrowanej treści HTTPS. Nie wykonujemy MITM.

## Ograniczenia widoczności

Źródło nazwy jest oznaczane jako DNS, korelacja DNS, HTTP Host, TLS SNI albo tylko IP wraz z poziomem pewności. DoH, ECH, DoT, QUIC, VPN i część ruchu przekazywanego przez ICS może ukryć nazwę lub sam przepływ. Zwykłe HTTPS nie oznacza dostępu do treści.

## Retencja i usuwanie

Pierścień telemetrii jest ulotny i ograniczony do bieżącego procesu. Panel udostępnia czyszczenie historii; restart usługi może ją usunąć. Nie traktuj danych jako dziennika audytowego ani dowodu kompletności.

## Sekrety i zgłoszenia

Konfiguracja i hasła są chronione przez DPAPI komputera. Bilet sesji nie powinien trafiać do repozytorium, zrzutów ekranu ani zgłoszeń. Przed przesłaniem diagnostyki usuń hasła, tokeny, nazwy użytkowników, nazwy hostów, adresy IP, domeny firmowe i dane plików.

Informacje o przetwarzaniu przez organizację pracodawcy należy uzgodnić z jej administratorem bezpieczeństwa/ochrony danych. WorkRouter nie zastępuje oceny prawnej ani polityki firmy.
