import { apiJson, send } from './client'
import type { ChoiceMode, SellBy } from './catalog'

export interface Contact {
  name: string | null
  phone: string | null
  address: string | null
  email: string | null
  openingHours: string | null
}

export interface Site {
  backgroundImageUrl: string | null
  deliveryEnabled: boolean
  pickupEnabled: boolean
  deliveryAreaText: string | null
  deliveryFeeText: string | null
  kashrutText: string | null
  paymentPhone: string | null
  contact: Contact
}

export interface MenuOption {
  id: number
  label: string
  amount: number
  price: number
  isDefault: boolean
}

export interface MenuDish {
  id: number
  name: string
  categoryId: number
  description: string | null
  allergenInfo: string | null
  sellBy: SellBy
  choiceMode: ChoiceMode
  minAmount: number | null
  maxAmount: number | null
  amountStep: number | null
  unitPrice: number | null
  isAddOnOnly: boolean
  isSoldOut: boolean
  options: MenuOption[]
  images: string[]
  addOnDishIds: number[]
}

export interface MenuCategory {
  id: number
  name: string
}

export interface SupplyDate {
  /** "yyyy-MM-dd" */
  date: string
  /** Local time ordering for this date closes, "yyyy-MM-ddTHH:mm:ss" */
  cutoff: string
}

export interface Menu {
  categories: MenuCategory[]
  dishes: MenuDish[]
  supplyDates: SupplyDate[]
}

export type Fulfillment = 'Delivery' | 'Pickup'
export type Payment = 'OnDelivery' | 'Transfer'

export interface OrderLineInput {
  dishId: number
  optionId: number | null
  quantity: number
  addOns: { dishId: number; optionId: number | null; quantity: number }[]
}

export interface OrderInput {
  phone: string
  name: string
  address: string
  supplyDate: string
  fulfillmentMethod: Fulfillment
  paymentMethod: Payment
  notes: string
  verificationToken: string
  items: OrderLineInput[]
}

export interface Confirmation {
  id: number
  supplyDate: string
  fulfillmentMethod: Fulfillment
  paymentMethod: Payment
  total: number
  paymentPhone: string | null
  items: {
    dishName: string
    optionLabel: string | null
    quantity: number
    unitPrice: number
    lineTotal: number
    isAddOn: boolean
  }[]
}

export const siteApi = {
  get: () => apiJson<Site>('/api/site'),
  menu: () => apiJson<Menu>('/api/menu'),
}

export const verificationApi = {
  /** Answers with devCode only when the server runs with WhatsApp__ShowCodeOnScreen (test setups). */
  send: (phone: string) =>
    apiJson<{ devCode?: string } | undefined>('/api/phone-verification/send', send('POST', { phone })),
  confirm: (phone: string, code: string) =>
    apiJson<{ token: string }>('/api/phone-verification/confirm', send('POST', { phone, code })),
}

export const ordersApi = {
  create: (input: OrderInput) => apiJson<Confirmation>('/api/orders', send('POST', input)),
}

export const contactsApi = {
  get: () => apiJson<Contact>('/api/admin/contact'),
  save: (contact: Contact) => apiJson<Contact>('/api/admin/contact', send('PUT', contact)),
  notifyPhones: () => apiJson<NotifyPhone[]>('/api/admin/notify-phones'),
  addNotifyPhone: (phone: string, name: string) =>
    apiJson<NotifyPhone>('/api/admin/notify-phones', send('POST', { phone, name })),
  removeNotifyPhone: (id: number) => apiJson<void>(`/api/admin/notify-phones/${id}`, send('DELETE')),
}

export interface NotifyPhone {
  id: number
  phone: string
  name: string | null
}
