# Build Water

**Автор:** AGA7ON  
**Версия:** 0.9.0

## Описание
Build Water добавляет устанавливаемые блоки воды с корректным водным объёмом. Мод влияет на землю (влажность и водная растительность у кромки воды) и позволяет мотыге/культиватору работать через водные блоки. Изменения конфига применяются сразу, без перезапуска игры.

## Особенности
- Устанавливаемые блоки воды с реальным объёмом.
- Влажная земля и водная растительность на границе воды.
- Мотыга/культиватор игнорируют коллизии водных блоков.
- Живые изменения конфига (без перезахода).

## Установка
1. Установите **BepInExPack для Valheim**.
2. Скопируйте `BuildWater.dll` в `BepInEx/plugins/`.
3. Запустите игру один раз, чтобы создать `BepInEx/config/buildwater.cfg`.

## Требования
- Valheim
- BepInExPack_Valheim

## Зависимости
- `denikson-BepInExPack_Valheim`

## Настройки
Файл конфига: `BepInEx/config/buildwater.cfg`
Основные параметры:
- `SurfacePaddingMeters` (может быть отрицательным)
- `TerrainInfluenceRadiusMeters`
- `TerrainInfluenceMaxDepthMeters`
- `PlayerCheckAboveSurface`
- `CurtainMinDepth`

## Авторство
Автор: AGA7ON
