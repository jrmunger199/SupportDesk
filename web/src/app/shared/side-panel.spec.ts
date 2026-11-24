import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { SidePanel } from './side-panel';

@Component({
  imports: [SidePanel],
  template: `
    <app-side-panel
      heading="Ticket details"
      [fullPage]="fullPage()"
      (closed)="closedCount = closedCount + 1"
    >
      <button type="button" id="inner">Inner action</button>
      <input id="field" />
    </app-side-panel>
  `,
})
class Host {
  readonly fullPage = signal<readonly unknown[] | undefined>(['/tickets', 'abc']);
  closedCount = 0;
}

describe('SidePanel', () => {
  let fixture: ComponentFixture<Host>;
  let host: Host;
  let root: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [Host], providers: [provideRouter([])] });
    fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  const dialog = () => root.querySelector('[role="dialog"]') as HTMLElement;
  const escape = (target: EventTarget = document.body, cancelled = false) => {
    const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    if (cancelled) event.preventDefault();
    target.dispatchEvent(event);
  };

  it('is a labelled modal dialog', () => {
    expect(dialog().getAttribute('aria-modal')).toBe('true');
    const labelId = dialog().getAttribute('aria-labelledby')!;
    expect(root.querySelector(`#${labelId}`)!.textContent).toBe('Ticket details');
  });

  it('shows what it is given', () => {
    expect(root.querySelector('#inner')!.textContent).toBe('Inner action');
  });

  it('closes from the close button, with a descriptive accessible name', () => {
    const button = root.querySelector('header button') as HTMLButtonElement;
    expect(button.getAttribute('aria-label')).toBe('Close ticket details');

    button.click();

    expect(host.closedCount).toBe(1);
  });

  it('closes when the dimmed page behind it is clicked', () => {
    (root.querySelector('.backdrop') as HTMLElement).click();

    expect(host.closedCount).toBe(1);
  });

  it('closes on Escape from inside the panel', () => {
    escape(root.querySelector('#field')!);

    expect(host.closedCount).toBe(1);
  });

  it('closes on Escape even when focus has fallen out of the panel onto the page', () => {
    escape(document.body);

    expect(host.closedCount).toBe(1);
  });

  it('leaves Escape alone when something inside already used it (like a suggestions list)', () => {
    escape(root.querySelector('#field')!, true);

    expect(host.closedCount).toBe(0);
  });

  it('ignores other keys', () => {
    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

    expect(host.closedCount).toBe(0);
  });

  it('offers a link to the full page, and hides it when there is none', () => {
    const link = root.querySelector('header a') as HTMLAnchorElement;
    expect(link.textContent).toContain('Open full page');
    expect(link.getAttribute('href')).toBe('/tickets/abc');

    host.fullPage.set(undefined);
    fixture.detectChanges();

    expect(root.querySelector('header a')).toBeNull();
  });

  it('gives each panel its own title id', () => {
    const second = TestBed.createComponent(Host);
    second.detectChanges();

    const ids = [fixture, second].map((f) => f.nativeElement.querySelector('h2').id);
    expect(new Set(ids).size).toBe(2);
  });

  it('pulls focus back into the panel if the focused control disappears', async () => {
    const button = root.querySelector('#inner') as HTMLButtonElement;
    button.focus();
    button.remove(); // the browser drops focus to <body> and reports focusout with no related target
    expect(document.activeElement).toBe(document.body);

    root
      .querySelector('header')!
      .dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: null }));
    await new Promise((resolve) => setTimeout(resolve));

    expect(document.activeElement).toBe(dialog());
  });

  it('does not steal focus when it moves to another real element', async () => {
    const field = root.querySelector('#field') as HTMLInputElement;
    field.focus();

    root
      .querySelector('header')!
      .dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: field }));
    await new Promise((resolve) => setTimeout(resolve));

    expect(document.activeElement).toBe(field);
  });
});
