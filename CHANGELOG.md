# PlateSolvePlus Changelog

## 1.0.0.3
## 🐛 Bugfixes & Verbesserungen
### Bugfix: Falsche Offset-Anzeige nach Reset
- Behoben: Nach dem Zurücksetzen des Offsets wurden in der UI weiterhin alte Offset-Werte angezeigt.
- Die Anzeige aktualisiert sich jetzt korrekt nach einem Reset.

### Bugfix: Falsche Berechnung der Quaternion
- Behoben: Die Quaternion wurde fälschlicherweise aus den alten Offset-Werten berechnet.
- Die Quaternion wird jetzt korrekt aus den aktuellen Rotationswerten berechnet.
- Die Quaternion-Property wurde so angepasst, dass sie immer die aktuellen Rotationswerte widerspiegelt.

### Verbesserte UI-Performance
- Optimierungen vorgenommen, um die UI-Reaktionsfähigkeit bei schnellen Offset-Änderungen zu verbessern.
- Reduzierung unnötiger UI-Updates durch verbesserte PropertyChanged-Logik.
- Die Offset-Anzeige in den **Options** aktualisiert sich jetzt effizienter.
- Die Live-Updates und Preview-Funktionalität wurden optimiert, um eine flüssigere Benutzererfahrung zu gewährleisten.

### Touch N Stars Integration verbessert
- Weitere Anpassungen vorgenommen, um die Kompatibilität mit Touch N Stars zu verbessern.
- Fehlerbehebungen und Optimierungen für die TnS API-Integration.
- Verbesserte Synchronisation der Offset-Daten zwischen PlateSolvePlus und Touch N Stars.
- Stabilitätsverbesserungen bei der Kommunikation mit Touch N Stars.

### üversetzungen aktualisiert
- Aktualisierte Übersetzungen für alle neuen und geänderten UI-Elemente. DE, EN

## 🔧 Offset-Handling & UI-Refactoring

### Offset-Logik vereinfacht
- Der Offset wird jetzt automatisch verwendet, sobald ein gültiger Offset gesetzt ist  
  (Rotation ≠ Identity oder ΔRA / ΔDec ≠ 0).
- Ein expliziter *Offset On/Off*-Schalter entfällt vollständig.

### CaptureOnly-Workflow bereinigt
- **CaptureOnly** führt ausschließlich ein Platesolve durch und verändert keinen Systemzustand mehr.
- Bei vorhandenem Offset werden korrigierte Koordinaten berechnet und mit der aktuellen Mount-Position verglichen.
- Abweichungen werden angezeigt, inklusive einer Empfehlung zur Offset-Rekalibrierung.

### Options.xaml als zentrale Offset-Ansicht
- Die Offset-Anzeige (Rotation, Quaternion, ΔRA / ΔDec, letzter Kalibrierzeitpunkt) wurde vollständig in die **Options** verschoben.
- Neuer Button **„Offset zurücksetzen“** zum vollständigen Reset des Offsets.

### PlatesolveplusDockableView entfernt
- Das veraltete Dockable inklusive UI-Logik wurde entkoppelt und kann vollständig entfernt werden.

### Live-UI-Updates für Offset-Werte
- Rotations- und Offset-Werte aktualisieren sich jetzt sofort in den **Options** nach Reset oder Kalibrierung.
- Korrekte `PropertyChanged`-Kaskaden für berechnete Properties stellen konsistente UI-Updates sicher.

### REST/API & Centering angepasst
- **Capture+Sync**, **Capture+Slew** und **Target+Center** nutzen den Offset ausschließlich, wenn ein gültiger Offset gesetzt ist.
- Centering ist nur möglich, wenn ein valider Offset vorhanden ist.

### Touch N Stars Integration
- APIs für TnS angepasst.


## 1.0.0.1
- Initial alpha release