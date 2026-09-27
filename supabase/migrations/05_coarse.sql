-- Family Together — posiciones aproximadas (2026-09-27, decisión de Josep).
--
-- Dentro de casa solo hay ubicación por red, con unos 100 m de precisión, y la regla de 25 m
-- (FR-010) dejaba al grupo sin ver a nadie que estuviera en interiores. Ahora, si en 10 minutos no
-- ha habido ninguna lectura buena, el móvil envía la mejor que tenga (hasta 100 m) marcada como
-- aproximada: actualiza la última posición del mapa (el trigger no distingue), pero el historial y
-- la detección de zonas siguen usando solo las de 25 m o mejores.
-- Relanzable.

alter table public.positions add column if not exists coarse boolean not null default false;

grant insert (coarse) on public.positions to authenticated;
