import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { of } from 'rxjs';
import { MentionService } from '../core/mention.service';
import { MentionCandidate } from '../core/models';
import { MentionTextarea } from './mention-textarea';

const PRIYA = '3f2a1c4e-0b5d-4c6e-8f7a-9b0c1d2e3f40';
const MARCUS = '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d';

const people: MentionCandidate[] = [
  { id: PRIYA, displayName: 'Priya Nair', role: 'Agent' },
  { id: MARCUS, displayName: 'Marcus Lee', role: 'Admin' },
];

@Component({
  imports: [ReactiveFormsModule, MentionTextarea],
  template: `<app-mention-textarea inputId="box" [formControl]="control" ticketId="t-1" />`,
})
class Host {
  readonly control = new FormControl('', { nonNullable: true });
}

describe('MentionTextarea', () => {
  let fixture: ComponentFixture<Host>;
  let host: Host;
  let box: HTMLTextAreaElement;
  const candidates = vi.fn();

  beforeEach(() => {
    candidates.mockReset().mockReturnValue(of(people));
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [{ provide: MentionService, useValue: { candidates } }],
    });
    fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    fixture.detectChanges();
    box = fixture.nativeElement.querySelector('textarea') as HTMLTextAreaElement;
  });

  /** Types the way a person does: the value changes, the caret sits at the end, an input event fires. */
  const type = (text: string) => {
    box.value = text;
    box.setSelectionRange(text.length, text.length);
    box.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };

  const press = (key: string) => {
    const event = new KeyboardEvent('keydown', { key, cancelable: true, bubbles: true });
    box.dispatchEvent(event);
    fixture.detectChanges();
    return event;
  };

  const options = () =>
    [...fixture.nativeElement.querySelectorAll('[role="option"]')].map((o) =>
      (o as HTMLElement).textContent!.replace(/\s+/g, ' ').trim(),
    );

  it('is labelled by the id it is given and is a combobox', () => {
    expect(box.id).toBe('box');
    expect(box.getAttribute('role')).toBe('combobox');
    expect(box.getAttribute('aria-expanded')).toBe('false');
  });

  it('suggests people as soon as @ is typed, loading them for the right ticket', () => {
    type('Hello @');

    expect(candidates).toHaveBeenCalledWith('t-1');
    expect(options()).toHaveLength(2);
    expect(options()[0]).toContain('Priya Nair');
    expect(options()[1]).toContain('Marcus Lee');
  });

  it('filters suggestions as more is typed and exposes the active option for screen readers', () => {
    type('Hello @mar');

    expect(options()).toHaveLength(1);
    expect(options()[0]).toContain('Marcus Lee');
    expect(box.getAttribute('aria-expanded')).toBe('true');
    expect(box.getAttribute('aria-activedescendant')).toMatch(/^mention-option-\d+-0$/);
  });

  it('picks the highlighted person with Enter, showing plain text but storing the token', () => {
    type('Hi @pri');

    const event = press('Enter');

    expect(event.defaultPrevented).toBe(true); // Enter must not insert a newline while choosing
    expect(box.value).toBe('Hi @Priya Nair ');
    expect(host.control.value).toBe(`Hi @[Priya Nair](${PRIYA}) `);
    expect(box.getAttribute('aria-expanded')).toBe('false');
  });

  it('moves through the list with the arrow keys and wraps around', () => {
    type('@');
    expect(options().length).toBe(2);

    press('ArrowDown');
    press('Enter');

    expect(host.control.value).toBe(`@[Marcus Lee](${MARCUS}) `);
  });

  it('also picks with Tab, and with a click', () => {
    type('@pri');
    press('Tab');
    expect(host.control.value).toContain(PRIYA);

    type('and @mar');
    (fixture.nativeElement.querySelector('[role="option"]') as HTMLElement).dispatchEvent(
      new MouseEvent('mousedown', { bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
    expect(host.control.value).toContain(MARCUS);
  });

  it('closes the list on Escape without picking anyone', () => {
    type('@pri');

    press('Escape');

    expect(box.getAttribute('aria-expanded')).toBe('false');
    expect(host.control.value).toBe('@pri');
  });

  it('does not treat an email address as a mention', () => {
    type('write to bob@pri');

    expect(options()).toEqual([]);
    expect(candidates).not.toHaveBeenCalled();
  });

  it('shows no list when nobody matches', () => {
    type('@zzz');

    expect(options()).toEqual([]);
    expect(box.getAttribute('aria-expanded')).toBe('false');
  });

  it('lets Enter insert a newline when no list is open', () => {
    type('plain text');

    expect(press('Enter').defaultPrevented).toBe(false);
  });

  it('stops counting a mention once its name is edited away', () => {
    type('@pri');
    press('Enter');
    expect(host.control.value).toContain(PRIYA);

    type('Hi @Priya'); // the name was cut short

    expect(host.control.value).toBe('Hi @Priya');
  });

  it('shows stored text as plain @Name and remembers who it refers to', () => {
    host.control.setValue(`Please ask @[Marcus Lee](${MARCUS}) soon`);
    fixture.detectChanges();

    expect(box.value).toBe('Please ask @Marcus Lee soon');

    type('Please ask @Marcus Lee soon!'); // an unrelated edit keeps the mention

    expect(host.control.value).toBe(`Please ask @[Marcus Lee](${MARCUS}) soon!`);
  });

  it('is cleared when the form is reset', () => {
    type('@pri');
    press('Enter');

    host.control.reset('');
    fixture.detectChanges();

    expect(box.value).toBe('');
  });

  it('can be disabled through the form control', () => {
    host.control.disable();
    fixture.detectChanges();

    expect(box.disabled).toBe(true);
  });
});
