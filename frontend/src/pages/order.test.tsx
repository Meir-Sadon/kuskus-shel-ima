import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { Confirmation } from '../api/site'
import { menu, menuDish, publicApi, site } from '../test/catalogData'
import { fakeApi, invalid } from '../test/fakeApi'
import { renderAt } from '../test/render'

const confirmation: Confirmation = {
  id: 7,
  supplyDate: '2026-10-09',
  fulfillmentMethod: 'Delivery',
  paymentMethod: 'OnDelivery',
  total: 82,
  paymentPhone: null,
  items: [
    { dishName: 'עוף בתנור', optionLabel: 'שלם', quantity: 1, unitPrice: 70, lineTotal: 70, isAddOn: false },
    { dishName: 'ירך', optionLabel: 'יחידה', quantity: 1, unitPrice: 12, lineTotal: 12, isAddOn: true },
  ],
}

const verification = {
  'POST /api/phone-verification/send': () => ({ status: 204 }),
  'POST /api/phone-verification/confirm': () => ({ token: 'proof' }),
}

async function openOrderPage(routes: Record<string, (...args: never[]) => unknown> = {}) {
  const api = fakeApi({ ...publicApi, ...verification, ...routes } as Parameters<typeof fakeApi>[0])
  renderAt('/')
  await screen.findByRole('heading', { level: 2, name: 'עופות' })
  return { api, user: userEvent.setup() }
}

const dishCard = (name: string) => screen.getByRole('article', { name })
const total = () => within(screen.getByRole('region', { name: 'סיכום הזמנה' }))

async function verifyPhone(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText('טלפון'), '050-123-4567')
  await user.click(screen.getByRole('button', { name: /שליחת קוד אימות/ }))
  await user.type(await screen.findByLabelText('קוד אימות'), '123456')
  await user.click(screen.getByRole('button', { name: 'אימות הטלפון' }))
  await screen.findByText('✓ הטלפון אומת')
}

async function fillAndSubmit(user: ReturnType<typeof userEvent.setup>) {
  await user.click(within(dishCard('עוף בתנור')).getByRole('button', { name: /הוספה להזמנה/ }))
  await user.type(screen.getByLabelText('שם מלא'), 'דנה')
  await user.type(screen.getByLabelText('כתובת למשלוח'), 'הרצל 1')
  await verifyPhone(user)
  await user.click(screen.getByRole('button', { name: 'שליחת ההזמנה' }))
}

beforeEach(() => {
  localStorage.clear()
})

describe('Order page', () => {
  it('groups dishes by category, all open, and hides add-on-only dishes', async () => {
    await openOrderPage()

    const groups = document.querySelectorAll('details.category')
    expect([...groups].map((g) => g.hasAttribute('open'))).toEqual([true, true])
    expect(screen.getByRole('heading', { level: 3, name: 'עוף בתנור' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { level: 3, name: 'ירך' })).not.toBeInTheDocument()
    expect(screen.getByText('בהשגחת הרבנות')).toBeInTheDocument()
  })

  it('shows the main contact in the footer', async () => {
    await openOrderPage()
    const footer = screen.getByRole('contentinfo')
    await waitFor(() => expect(within(footer).getByRole('link', { name: '050-1234567' })).toHaveAttribute('href', 'tel:0501234567'))
  })

  it('shows the dish details only once it is chosen, with the default option preselected', async () => {
    const { user } = await openOrderPage()
    const card = dishCard('עוף בתנור')
    expect(within(card).queryByRole('radio')).not.toBeInTheDocument()

    await user.click(within(card).getByRole('button', { name: /הוספה להזמנה/ }))

    expect(within(card).getByRole('radio', { name: /שלם/ })).toBeChecked()
    expect(within(card).getByRole('radio', { name: /חצי/ })).not.toBeChecked()
    expect(total().getByText('סה"כ: ₪70')).toBeInTheDocument()
  })

  it('keeps the total up to date with options, amounts and add-ons', async () => {
    const { user } = await openOrderPage()
    const card = dishCard('עוף בתנור')
    await user.click(within(card).getByRole('button', { name: /הוספה להזמנה/ }))

    await user.click(within(card).getByRole('radio', { name: /חצי/ }))
    await user.click(within(card).getByRole('button', { name: 'הוספה: עוף בתנור' }))
    expect(total().getByText('סה"כ: ₪80')).toBeInTheDocument()

    await user.click(within(card).getByRole('button', { name: 'הוספה: ירך' }))
    await user.click(within(card).getByRole('button', { name: 'הוספה: ירך' }))
    expect(total().getByText('סה"כ: ₪104')).toBeInTheDocument()

    const meat = dishCard('בשר טחון')
    await user.click(within(meat).getByRole('button', { name: /הוספה להזמנה/ }))
    await user.click(within(meat).getByRole('button', { name: 'הוספה: בשר טחון' }))
    expect(total().getByText('סה"כ: ₪171.5')).toBeInTheDocument()
    expect(total().getByText('2 מנות')).toBeInTheDocument()
  })

  it('stops a free amount at the admin range', async () => {
    const { user } = await openOrderPage()
    const meat = dishCard('בשר טחון')
    await user.click(within(meat).getByRole('button', { name: /הוספה להזמנה/ }))

    expect(within(meat).getByRole('button', { name: 'הפחתה: בשר טחון' })).toBeDisabled()
    for (let i = 0; i < 10; i++) await user.click(within(meat).getByRole('button', { name: 'הוספה: בשר טחון' }))
    expect(within(meat).getByRole('button', { name: 'הוספה: בשר טחון' })).toBeDisabled()
    expect(within(meat).getByText(/3 ק"ג/)).toBeInTheDocument()
  })

  it('resets the whole order to the defaults', async () => {
    const { user } = await openOrderPage()
    await user.click(within(dishCard('עוף בתנור')).getByRole('button', { name: /הוספה להזמנה/ }))
    await user.type(screen.getByLabelText('הערות (לא חובה)'), 'בלי חריף')

    await user.click(screen.getByRole('button', { name: 'איפוס ההזמנה' }))

    expect(total().getByText('סה"כ: ₪0')).toBeInTheDocument()
    expect(screen.getByLabelText('הערות (לא חובה)')).toHaveValue('')
    expect(within(dishCard('עוף בתנור')).queryByRole('radio')).not.toBeInTheDocument()
  })

  it('offers only the next open supply dates, with the pay-on-delivery sentence by default', async () => {
    await openOrderPage()
    const select = screen.getByLabelText('יום אספקה')
    expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'יום שישי, 09/10/2026',
      'יום שישי, 16/10/2026',
    ])
    expect(screen.getByText('התשלום יבוצע במעמד מסירת המשלוח')).toBeInTheDocument()
  })

  it('hides the Bit / PayBox option when the admin set no payment phone', async () => {
    await openOrderPage({ 'GET /api/site': () => site({ paymentPhone: null }) })
    expect(screen.queryByRole('radio', { name: /Bit/ })).not.toBeInTheDocument()
  })

  it('needs a confirmed phone before the order is sent', async () => {
    const { api, user } = await openOrderPage()
    await user.click(within(dishCard('עוף בתנור')).getByRole('button', { name: /הוספה להזמנה/ }))
    await user.type(screen.getByLabelText('שם מלא'), 'דנה')
    await user.type(screen.getByLabelText('כתובת למשלוח'), 'הרצל 1')
    await user.type(screen.getByLabelText('טלפון'), '0501234567')

    await user.click(screen.getByRole('button', { name: 'שליחת ההזמנה' }))

    expect(await screen.findByLabelText('טלפון')).toHaveAccessibleDescription(/צריך לאמת את הטלפון בקוד שנשלח ב־WhatsApp\./)
    expect(api.sent('POST', '/api/orders')).toHaveLength(0)
  })

  it('shows the code on screen when the server runs in test mode', async () => {
    const { user } = await openOrderPage({ 'POST /api/phone-verification/send': () => ({ devCode: '483920' }) })
    await user.type(screen.getByLabelText('טלפון'), '0501234567')
    await user.click(screen.getByRole('button', { name: /שליחת קוד אימות/ }))

    expect(await screen.findByText('מצב בדיקה: קוד האימות הוא 483920')).toBeInTheDocument()
  })

  it('shows the code error from the server', async () => {
    const { user } = await openOrderPage({ 'POST /api/phone-verification/confirm': () => invalid({ Code: ['codeWrong'] }) })
    await user.type(screen.getByLabelText('טלפון'), '0501234567')
    await user.click(screen.getByRole('button', { name: /שליחת קוד אימות/ }))
    await user.type(await screen.findByLabelText('קוד אימות'), '000000')
    await user.click(screen.getByRole('button', { name: 'אימות הטלפון' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('הקוד שגוי.')
  })

  it('sends the order, then shows the success popup with the summary', async () => {
    const { api, user } = await openOrderPage({ 'POST /api/orders': () => confirmation })
    await fillAndSubmit(user)

    const dialog = await screen.findByRole('dialog', { name: 'ההזמנה התקבלה' })
    expect(within(dialog).getByText('מספר ההזמנה: 7')).toBeInTheDocument()
    expect(within(dialog).getByText(/ירך/)).toBeInTheDocument()
    expect(within(dialog).getByText('₪82')).toBeInTheDocument()
    expect(within(dialog).getByText('התשלום יבוצע במעמד מסירת המשלוח')).toBeInTheDocument()

    expect(api.sent('POST', '/api/orders')[0].body).toEqual({
      phone: '0501234567',
      name: 'דנה',
      address: 'הרצל 1',
      supplyDate: '2026-10-09',
      fulfillmentMethod: 'Delivery',
      paymentMethod: 'OnDelivery',
      notes: '',
      verificationToken: 'proof',
      items: [{ dishId: 1, optionId: 11, quantity: 1, addOns: [] }],
    })

    await userEvent.click(within(dialog).getByRole('button', { name: 'סגירה' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(total().getByText('סה"כ: ₪0')).toBeInTheDocument()
  })

  it('shows the Bit / PayBox number after a transfer order', async () => {
    const { user } = await openOrderPage({
      'POST /api/orders': () => ({ ...confirmation, paymentMethod: 'Transfer', paymentPhone: '052-9999999' }),
    })
    await user.click(screen.getByRole('radio', { name: /Bit/ }))
    await fillAndSubmit(user)

    const dialog = await screen.findByRole('dialog', { name: 'ההזמנה התקבלה' })
    expect(within(dialog).getByText('052-9999999')).toBeInTheDocument()
    expect(within(dialog).queryByText('התשלום יבוצע במעמד מסירת המשלוח')).not.toBeInTheDocument()
  })

  it('keeps the order and explains when the server refuses it', async () => {
    const { user } = await openOrderPage({
      'POST /api/orders': () => invalid({ SupplyDate: ['supplyDateUnavailable'] }),
    })
    await fillAndSubmit(user)

    expect(await screen.findByRole('alert')).toHaveTextContent('יום האספקה שנבחר כבר לא פתוח להזמנה')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(total().getByText('סה"כ: ₪70')).toBeInTheDocument()
  })

  describe('drafts', () => {
    it('asks whether to save when leaving with an order in progress, and restores a saved draft', async () => {
      const { user } = await openOrderPage()
      await user.click(within(dishCard('עוף בתנור')).getByRole('button', { name: /הוספה להזמנה/ }))
      await user.click(within(dishCard('עוף בתנור')).getByRole('button', { name: 'הוספה: עוף בתנור' }))

      await user.click(screen.getByRole('link', { name: 'המלצות' }))
      const dialog = await screen.findByRole('dialog', { name: 'ההזמנה עוד לא נשלחה' })
      await user.click(within(dialog).getByRole('button', { name: 'שמירה ויציאה' }))
      expect(await screen.findByRole('heading', { level: 1, name: 'המלצות' })).toBeInTheDocument()
      expect(localStorage.getItem('kuskus.orderDraft')).toContain('"quantity":2')

      await user.click(screen.getByRole('link', { name: 'הזמנה' }))
      expect(await screen.findByText('שחזרנו הזמנה שהתחלתם ושמרתם.')).toBeInTheDocument()
      expect(total().getByText('סה"כ: ₪140')).toBeInTheDocument()
    })

    it('discards the draft when leaving without saving', async () => {
      localStorage.setItem('kuskus.orderDraft', JSON.stringify({ selections: { 1: { optionId: 11, quantity: 1, addOns: {} } } }))
      const { user } = await openOrderPage()

      await user.click(screen.getByRole('link', { name: 'פרופיל' }))
      await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'יציאה בלי לשמור' }))

      expect(await screen.findByRole('heading', { level: 1, name: 'פרופיל' })).toBeInTheDocument()
      expect(localStorage.getItem('kuskus.orderDraft')).toBeNull()
    })

    it('stays on the page when the client changes their mind', async () => {
      const { user } = await openOrderPage()
      await user.click(within(dishCard('עוף בתנור')).getByRole('button', { name: /הוספה להזמנה/ }))

      await user.click(screen.getByRole('link', { name: 'פרופיל' }))
      await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'להישאר בהזמנה' }))

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      expect(screen.getByRole('heading', { level: 1, name: 'הזמנה' })).toBeInTheDocument()
    })

    it('leaves freely when nothing is ordered', async () => {
      const { user } = await openOrderPage()
      await user.click(screen.getByRole('link', { name: 'פרופיל' }))
      expect(await screen.findByRole('heading', { level: 1, name: 'פרופיל' })).toBeInTheDocument()
    })

    it('tells the client when saved dishes are gone', async () => {
      localStorage.setItem(
        'kuskus.orderDraft',
        JSON.stringify({
          selections: { 1: { optionId: 11, quantity: 1, addOns: {} }, 99: { optionId: 1, quantity: 1, addOns: {} } },
        }),
      )
      await openOrderPage()

      expect(await screen.findByText(/מנה אחת כבר לא זמינה והוסרה/)).toBeInTheDocument()
      expect(total().getByText('סה"כ: ₪70')).toBeInTheDocument()
    })
  })

  it('shows sold-out dishes as sold out, without an add button', async () => {
    await openOrderPage({
      'GET /api/menu': () => menu({ dishes: [menuDish(1, 'עוף בתנור', 1, { isSoldOut: true }), menuDish(2, 'אורז', 2)] }),
    })
    const card = dishCard('עוף בתנור')
    expect(within(card).getByText('אזל')).toBeInTheDocument()
    expect(within(card).queryByRole('button')).not.toBeInTheDocument()
  })

  it('lets each category be collapsed', async () => {
    const { user } = await openOrderPage()
    const summary = screen.getByRole('heading', { level: 2, name: 'עופות' }).closest('summary')!
    await user.click(summary)
    expect(summary.closest('details')).not.toHaveAttribute('open')
  })

  it('rotates a dish carousel and lets it be paused', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      const withImages = menu({
        dishes: [menuDish(1, 'עוף בתנור', 1, { images: ['/a.jpg', '/b.jpg'] }), menuDish(2, 'אורז', 2)],
      })
      const { user } = await openOrderPage({ 'GET /api/menu': () => withImages })
      const image = () => within(dishCard('עוף בתנור')).getByRole('img')
      expect(image()).toHaveAttribute('src', '/a.jpg')

      await act(() => vi.advanceTimersByTimeAsync(4600))
      expect(image()).toHaveAttribute('src', '/b.jpg')

      await user.click(screen.getByRole('button', { name: 'עצירת גלגול התמונות של עוף בתנור' }))
      await act(() => vi.advanceTimersByTimeAsync(10_000))
      expect(image()).toHaveAttribute('src', '/b.jpg')
      expect(screen.getByRole('button', { name: 'הפעלת גלגול התמונות של עוף בתנור' })).toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  })
})
