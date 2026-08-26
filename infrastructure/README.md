# Infrastructure

## Навигация

`caddy/` содержит gateway-конфигурации `Caddyfile`, `Caddyfile.lan` и
`Caddyfile.prod`. Compose/Server Bundle монтирует только нужный профиль; API,
PostgreSQL и NATS не должны становиться публичными напрямую.

## Эксплуатация

Изменяйте gateway только через release bundle и проверяйте compose config,
TLS/host policy и authenticated readiness. Не помещайте в Caddyfiles секреты,
токены или пользовательские данные. Локальный Desktop не использует
infrastructure напрямую.
