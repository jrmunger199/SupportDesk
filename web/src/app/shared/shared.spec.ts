import { TestBed } from '@angular/core/testing';
import { PriorityBadge, RoleBadge, StatusBadge } from './badges';
import { TimeAgoPipe } from './time-ago.pipe';

describe('TimeAgoPipe', () => {
  const pipe = new TimeAgoPipe();
  const now = Date.parse('2026-11-24T12:00:00Z');
  const ago = (ms: number) => new Date(now - ms).toISOString();

  it.each([
    [10_000, 'just now'],
    [5 * 60_000, '5 minutes ago'],
    [60 * 60_000, '1 hour ago'],
    [3 * 3_600_000, '3 hours ago'],
    [24 * 3_600_000, 'yesterday'],
    [3 * 24 * 3_600_000, '3 days ago'],
  ])('formats %i ms ago as "%s"', (ms, expected) => {
    expect(pipe.transform(ago(ms), now)).toBe(expected);
  });

  it('handles empty input', () => {
    expect(pipe.transform(null)).toBe('');
    expect(pipe.transform(undefined)).toBe('');
  });

  it('reads API timestamps as UTC regardless of the local time zone', () => {
    expect(pipe.transform('2026-11-24T11:55:00Z', now)).toBe('5 minutes ago');
  });
});

describe('badges', () => {
  it('shows a readable label for each status', () => {
    const fixture = TestBed.createComponent(StatusBadge);
    fixture.componentRef.setInput('status', 'InProgress');
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('.badge') as HTMLElement;
    expect(badge.textContent).toBe('In progress');
    expect(badge.dataset['status']).toBe('InProgress');
  });

  it('shows the priority', () => {
    const fixture = TestBed.createComponent(PriorityBadge);
    fixture.componentRef.setInput('priority', 'Urgent');
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('.badge') as HTMLElement;
    expect(badge.textContent).toBe('Urgent');
    expect(badge.dataset['priority']).toBe('Urgent');
  });

  it('shows the role', () => {
    const fixture = TestBed.createComponent(RoleBadge);
    fixture.componentRef.setInput('role', 'Admin');
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Admin');
  });
});
