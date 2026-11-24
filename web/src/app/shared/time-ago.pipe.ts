import { Pipe, PipeTransform } from '@angular/core';

const UNITS: readonly [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 3600],
  ['month', 30 * 24 * 3600],
  ['day', 24 * 3600],
  ['hour', 3600],
  ['minute', 60],
];

const formatter = new Intl.RelativeTimeFormat('en', { numeric: 'auto' });

/** "5 minutes ago", "yesterday". Timestamps from the API are ISO 8601 in UTC. */
@Pipe({ name: 'timeAgo' })
export class TimeAgoPipe implements PipeTransform {
  transform(value: string | Date | null | undefined, now: number = Date.now()): string {
    if (!value) {
      return '';
    }

    const seconds = Math.round((new Date(value).getTime() - now) / 1000);
    const abs = Math.abs(seconds);

    if (abs < 45) {
      return 'just now';
    }

    for (const [unit, size] of UNITS) {
      if (abs >= size) {
        return formatter.format(Math.round(seconds / size), unit);
      }
    }

    return formatter.format(Math.round(seconds / 60), 'minute');
  }
}
