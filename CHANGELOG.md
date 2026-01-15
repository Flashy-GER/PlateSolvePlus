# PlateSolvePlus

## 1.0.0.2
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