package main

import (
	"bufio"
	"bytes"
	"crypto/aes"
	"crypto/ed25519"
	cryptoRandom "crypto/rand"
	"crypto/sha256"
	"crypto/sha512"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"log"
	"math/big"
	"math/rand"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/btcsuite/btcd/btcutil/base58"
	"github.com/tyler-smith/go-bip39"
	"golang.org/x/crypto/pbkdf2"
)

var syncer *sync.Mutex

type Contract interface {
	Transfer()
	Mint()
	Burn()
}

type AccountID string
type WalletID []byte // actor address
type TxState string
type Step struct {
	ID  uint32
	Job func(chan struct{}) // empty channel as signal
}

type Position struct {
	PositionType int8 // 0 means buy 1 means sell
	OpenedAt     int64
}
type Pipeline struct {
	Stages []Stage
}

type Stage struct {
	Steps []Step
}

// instead of storing tx object just build by shifting the bits
// to point to the location where the tx might be in future
type Transaction struct {
	Payer  AccountID
	Signer AccountID
	Owner  AccountID
	Caller AccountID
	Tokens []Token
	GasFee Token
	// TreasuryType
	// TxAction
	// TxType
	Source      WalletID // src to dest via chan
	Destination WalletID //
	State       TxState
	Pipeline    []Pipeline
	Hash        string
	Signature   string
	Data        [200]byte // transaction data can stores up to 200 bytes
	Time        time.Time
	// Status        TxStatus
	Confirmations int
	Memo          []byte
}

type Block struct {
	ID           string
	Hash         [32]byte       // sha256 bits hash
	PrevHash     [32]byte       // sha256 bits hash
	Transactions *[]Transaction // pointer to underlying tx data
}

// a token contains the amount and symbol
type Token struct {
	Amount uint64
	Symbol string
}

// repeating random events over time makes everything deterministic
type Reality struct{}  // a quantum object which is the result of the observation who received energy paulses
type Energy struct{}   // used to send the energy paulse to the selected path
type Decision struct{} // used to inject the energy to an specific path
type Paths struct{}    // contains all the paths probs

type Layer struct {
	Index         uint8
	EncryptionKey []byte
	EncryptedData []byte
	Password      []byte
}

var sycner sync.Mutex
var edSeed []byte
var finalSalt []byte
var shift atomic.Uint32

// note that pass the hashed data to this function to avoid leaking raw data while processing
func onionize(data []byte, layers uint32) (lastLayer Layer) {

	// === 1) hash of data
	// hasher := sha256.New()
	// hasher.Write(data)
	// hash := hasher.Sum(nil)
	hash := data

	// === 2) sign the hash
	randomBytes := make([]byte, 32) // 32 random bytes
	cryptoRandom.Read(randomBytes)
	mnemo, _ := bip39.NewMnemonic(randomBytes)
	seed := bip39.NewSeed(mnemo, "")
	wallet := ed25519.NewKeyFromSeed(seed) // ed25519 prvkey
	genSignature, _ := wallet.Sign(nil, hash, nil)
	syncer.Lock()
	edSeed = seed // ********** this seed will be used to create wallet
	syncer.Unlock()

	// === 3) genesis password and salt
	hash = append(hash, 0xff)
	genPassword := []byte(hex.EncodeToString(hash))                             // **********
	salt, _ := hex.DecodeString(fmt.Sprintf("%d", time.Now().Second()>>32|0&1)) // **********
	syncer.Lock()
	finalSalt = salt
	syncer.Unlock()

	layerChan := make(chan Layer, layers)
	waiter := sync.WaitGroup{}
	semChan := make(chan struct{}, layers)

	// 32 bytes encrypted data
	password := make([]byte, 32)
	encSign := make([]byte, 32)
	for lidx := range layers {
		waiter.Add(1)
		semChan <- struct{}{}
		// spawn thread for each layer
		go func() {
			defer func() {
				waiter.Done()
				<-semChan
			}()

			var encKey []byte
			if lidx == 0 {
				// === mutate the password and encSign
				syncer.Lock()
				password = genPassword
				encKey = pbkdf2.Key(password, salt, 4096, 32, sha512.New)
				cipher, _ := aes.NewCipher(encKey)
				cipher.Encrypt(encSign, genSignature) // for genesis layer we'll encrypt the genesis signature
				syncer.Unlock()
				// ===
			} else {
				lastLayer := <-layerChan // consume the last layer
				lastEncData := hex.EncodeToString(lastLayer.EncryptedData)
				lastEncKey := pbkdf2.Key(lastLayer.Password, salt, 4096, 32, sha512.New)
				lastEncDataKey := fmt.Sprintf("%s<>%s", lastEncData, hex.EncodeToString(lastEncKey)) // this should gets encrypted
				// === mutate the password and encSign
				syncer.Lock()
				password = encSign                                        // encSign contains the encrypted data (last data + last key) for last layer
				encKey = pbkdf2.Key(password, salt, 4096, 32, sha512.New) // use encSign as password
				cipher, _ := aes.NewCipher(encKey)
				cipher.Encrypt(encSign, []byte(lastEncDataKey)) // encrypt the last data + last key into encSign
				syncer.Unlock()
				// ===
			}
			layerChan <- Layer{
				Index:         uint8(lidx),
				EncryptedData: encSign,
				Password:      password,
			}
		}()
	}

	waiter.Wait()
	layer := <-layerChan

	// deonionization
	// deonionize(data, 8, layer, edSeed, finalSalt)
	return layer

}

// note that pass the hashed data to this function to avoid leaking raw data while processing
func deonionize(data []byte, layers uint32, lastLayer Layer, seed []byte, salt []byte) bool {

	// hasher := sha256.New()
	// hasher.Write(data)
	// hash := hasher.Sum(nil)
	hash := data

	lastLayerEncKey := pbkdf2.Key(lastLayer.Password, salt, 4096, 32, sha512.New) // use encSign as password
	lastLayerEncData := lastLayer.EncryptedData
	waiter := sync.WaitGroup{}
	syncer := sync.Mutex{}
	rawData := make(chan []byte, 1) // 1 buffer only if the gen signature is found
	for lidx := layers; lidx <= 0; lidx-- {
		lastLayerRawData := make([]byte, 32)        // defining per each thread
		cipher, _ := aes.NewCipher(lastLayerEncKey) // defining per each thread with new key
		waiter.Add(1)
		go func() {
			defer waiter.Done()
			syncer.Lock()
			cipher.Decrypt(lastLayerRawData, lastLayerEncData) // lastLayerRawData is getting mutated
			lastLayerRawDataString := string(lastLayerRawData) // cause it was []byte(lastEncDataKey)
			if strings.Contains(lastLayerRawDataString, "<>") {
				slipttedRawDataString := strings.Split(lastLayerRawDataString, "<>")
				lastLayerEncData, _ = hex.DecodeString(slipttedRawDataString[0]) // is getting mutated
				lastLayerEncKey, _ = hex.DecodeString(slipttedRawDataString[1])  // is getting mutated
				syncer.Unlock()
			} else { // the last layer doesnt contain <> so its the genesis signature
				rawData <- lastLayerRawData
			}
		}()
	}
	genesisSignature := <-rawData
	seedReader := bytes.NewReader(seed) // seed must have read method
	pubkey, _, _ := ed25519.GenerateKey(seedReader)
	return ed25519.Verify(pubkey, hash, genesisSignature)

}

type Packet struct {
	Data []byte
	TTL  time.Time
}

func (p *Packet) DropPacket(bits uint32) error { // p is mutable pointer to Packet for mutating the underlying data
	if p.TTL.Equal(time.Now()) {
		return fmt.Errorf("packet must leave!, too late for dropping bits!")
	}

	// =========== convert to bits
	extractedBits := make([]byte, len(p.Data)*8)
	for pidx := 0; pidx < len(p.Data); pidx++ {
		for b := range 8 { // we should do this for every byte 8 times
			/*--------*/ bit := (p.Data[pidx] >> b) & 0x01 /*--------*/ // convert to lsb bits
			extractedBits = append(extractedBits, bit)
		}
	}
	extractedBits = append(extractedBits, extractedBits[bits+1:]...) // append all elems from bits+1 up to end

	// =========== convert to bytes
	newData := make([]byte, len(p.Data)/8)
	batchSize := 8
	for bidx := 0; bidx < len(extractedBits); bidx += batchSize {
		// batching packing and chunking the bits
		end := min(bidx+batchSize, len(extractedBits))
		pack := extractedBits[bidx:end]
		b := uint8(0) // 1 byte u8
		for pidx := 0; pidx < len(pack); pidx++ {
			/*--------*/ b = b | pack[pidx]<<pidx /*--------*/ // convert back to byte
		}
		newData = append(newData, b)
	}
	lastOnionLayer := onionize(newData, 8)
	p.Data = lastOnionLayer.EncryptedData // p itself is mutable pointer not p.Data to deref it to put newData in it
	// this will mutate the p value itself won't change the underlying address of p pointer
	// *p = Packet{}
	// // this will mutate the p and change the underlying address with a new one of &Packet{}
	// p = &Packet{}
	return nil
}

func chunking() {
	semChan := make(chan struct{}, 2) // 2 threads at a time
	waiter := sync.WaitGroup{}
	sigChan := make(chan struct{}, 1)

	// task chunking between threads
	type Task func()
	batchChan := make(chan []Task, 100)
	totalTasks, batchSize := 20, 5
	tasks := make([]Task, 100)
	for i := 0; i < (totalTasks / batchSize); i++ {
		// 0 * 5 : 0 + 1 * 5 = [0:5]
		// 1 * 5 : 1 + 1 * 5 = [5:10]
		// 2 * 5 : 2 + 1 * 5 = [10:15]
		// 3 * 5 : 3 + 1 * 5 = [15:20]
		batchChan <- tasks[i*batchSize : (i+1)*batchSize]
	}
	handleBatch := func(batch []Task) {
		defer func() {
			waiter.Done()
			<-semChan // once the thread finishes its job release the slot from chan
		}()
		// ...
	}
	consumer := func(processor func(batch []Task)) {
		// receive from chan
		for {
			select {
			case batch := <-batchChan:
				semChan <- struct{}{} // spawn 2 thread at a time, if blocks we must wait for the thread to be released
				waiter.Add(1)
				go processor(batch)
			case <-sigChan: // receive terminate signal; on sending to channel and closing (zero value of default channel type) channel we'll receive from channel
				return
			default:
				log.Printf("[*] waiting to receive something...")
			}
		}
	}
	go consumer(handleBatch) // handle batch is a callback function to process each batch of tasks in a thread like caching on redis or store each received data in db
	close(sigChan)           // send terminate signal to consumer to stop receiving from chan
	waiter.Wait()            // wait here for all threads to be finished; it waits for each thread defer calling which call Done() method
}

func Permutations(source []string) {

	// Fisher–Yates shuffle to produce all permutations
	m := len(source)
	for i := range m {
		source[i] = source[rand.Intn(m)]
	}

	/*
		string -> bytes -> hash -> bytes -> hex -> base58/64 -> bits -> bigInt | bigInt -> hex -> bytes -> base58/64 -> bits
		show 32 bytes number in 256 bits in blocks using bitMasking and bitWising (a number in n bits can be shown in 2^n combos)
		append first 4 bits of sha256 of each entropy to the 128 bits or 256 bits
		create 12 or 24 packs of 11 bits then convert each 11 bits into word index
		Heap, RainBow, BloomFilter, PermAlgos, on/off Bits, unique m from n, unique m from m
	*/

	key := "1"
	hashTable := make(map[string]string)
	hasher := sha256.New()

	// pointer shifting codec
	// (img > base6458 > hash > hex || pointer hex > bytes > bits > base6458)
	file, _ := os.Open("img.png") // we can open this with an encoder to compress the bytes
	fileBuffer := make([]byte, 1024)
	file.Read(fileBuffer)
	encodedBuffer := bytes.NewBuffer([]byte{}) // bytes.Buffer implements io writer method
	encodedContent := base64.NewEncoder(&base64.Encoding{}, encodedBuffer)
	encodedContent.Write(fileBuffer) // fill the buffer
	hasher.Write([]byte(fileBuffer))
	hash := hasher.Sum(nil)
	hashTable[hex.EncodeToString(hash)] = key

	encodedImg := base58.Encode(fileBuffer)
	base58Bytes := []byte(encodedImg)
	hasher.Write(base58Bytes)
	imgHash := hasher.Sum(nil)
	imgHashHex := hex.EncodeToString(imgHash) // use this as prvkey
	log.Printf("img as prvkey > %s", imgHashHex)

	// perm algos
	set := []string{"wildonion"}
	n := 5
	// filling set with random words
	for i := 0; i < 1000; i++ {
		lastElem := set[len(set)-1]
		lastElemBytes := []byte(lastElem)
		randAscii := rand.Intn(128) + 1
		lastElemBytes[rand.Intn(len(lastElem))] = uint8(randAscii)
		lastElemBackString := string(lastElemBytes)
		set = append(set, lastElemBackString)
	}

	// all n combos from len(set) wihtout duplicates
	// =============== perm streaming algo with bitmasking
	combos := [][]byte{}
	waiter := sync.WaitGroup{}
	total := len(set)
	workers := 10
	batchSize := total / workers
	batchChan := make(chan []byte, 100)   // 100 data at a time
	comboChan := make(chan [][]byte, 100) // 100 data at a time
	for i := 0; i < len(set); i += batchSize {
		end := min(i+batchSize, total)
		batch := set[i:end]
		waiter.Add(1)
		// bit masks producer
		go func() {
			defer waiter.Done()
			comboIndex := i % len(batch)
			bits := make([]byte, len(batch)) // 0,1,0,0,1,1,1
			// extracting bits from a number
			for bidx := 0; bidx < len(batch); bidx++ { // n bits each bit represents the index inside the set 0 means no 1 means yes
				bits[bidx] = uint8(comboIndex >> bidx & 0x01)
			}
			batchChan <- bits
		}()
	}
	waiter.Add(1)
	// bit masks consumer; push them into combos
	go func() {
		defer waiter.Done()
		for bits := range batchChan {
			syncer.Lock()
			combos = append(combos, bits)
			syncer.Unlock()
			comboChan <- combos
		}
	}()
	// for selecting n combos from m total 1s must be n
	allNCombos := [][]string{}
	waiter.Add(1)
	go func() {
		defer waiter.Done()
		for combos := range comboChan {
			for _, comb := range combos {
				stringComb := []string{}
				if countOnes(comb) == uint32(n) {
					for idx, bit := range comb {
						if bit == 1 {
							syncer.Lock()
							stringComb = append(stringComb, set[idx])
							syncer.Unlock()
						}
					}
					syncer.Lock()
					allNCombos = append(allNCombos, stringComb)
					syncer.Unlock()
				}
			}
		}
	}()
	waiter.Wait() // wait for threads to finish creating combos

	// =============== perm algo with recursive backtracking
	// recursion handles the process repeatedly by calling itself
	// during the execution to update its own state
	var allNCombos2 [][]string
	// copy is like cloning
	var backtrack func(start int, combo []string)
	backtrack = func(start int, combo []string) { // combo is a combination with len n
		// base condition: if we reach n combos simply push it into the results then return
		if len(combo) == n {
			tmp := make([]string, n)
			copy(tmp, combo) // it's like cloning: combo.clone();
			syncer.Lock()
			allNCombos2 = append(allNCombos2, tmp)
			syncer.Unlock()
			// if n elems was in there we'll return from this function for the passed in start index
			// but the entire loop must gets executed this means all elems will be used to create
			// n pair of combos
			return
		}
		// iterate from 0 up to total elems in set to create all two pair with i
		// like [a, b] , [a, c] , [a, d] , ... for [a, b, c, d]
		for i := start; i < len(set); i++ {
			combo = append(combo, set[i]) // push each element in combo
			backtrack(i+1, combo)         // running this function again for every next element from i + 1
			combo = combo[:len(combo)-1]  // keep only 1 elem in combo
		}
	}
	backtrack(0, []string{})
	fmt.Println(allNCombos2)

}

func countOnes(combo []byte) uint32 {
	ones := 0
	for _, bit := range combo {
		if bit == 1 {
			ones++
		}
	}
	return uint32(ones)
}

type BufferData struct {
	Data []byte
}

func (buffer *BufferData) encode() []byte {
	// the following shifting causes us data loss and only the lower
	// 8 bits are preserved after masking with 0xff, instead we'll use
	// bitwise rotation
	// encodedBuf[idx] = buf[idx] << byte(bigIntNumber) & 0xff
	encoded := make([]byte, len(buffer.Data))
	for idx := range len(buffer.Data) {
		encoded[idx] = (buffer.Data[idx] << byte(shift.Load())) | (buffer.Data[idx] >> (8 - shift.Load()))
	}
	return encoded
}

func (buffer *BufferData) decode() []byte {
	decoded := make([]byte, len(buffer.Data))
	for idx := range len(buffer.Data) {
		decoded[idx] = (buffer.Data[idx] >> byte(shift.Load())) | (buffer.Data[idx] << (8 - byte(shift.Load())))
	}
	return decoded
}

func codeconion() {
	type Codec interface {
		encode()
		decode()
	}
	type Encoder struct{}
	type Decoder struct{}
	// buffer random bytes then hex then big int then bytes then bits using bitMasking
	_ = []byte{0x01}                  // 2 hex chars is 1 bytes or 8 bits
	_ = []byte{0x01, 0x02, 0x03, 0x4} // 0x01020304 8 hex chars means 4 bytes or 32 bits

	workerCounter := atomic.Uint32{}
	/*
		hints:
		------------
		x << 50 is x * 2^50 and x >> 50 is x / 2^50
		x >> 50 & 0xff extract the bits
		x >> i & 1 extract the ith bits from the x
		divide the 32 bytes number into 4 packs of 64 bits number := extract all 32 bytes from a 256 bits num like: num >> 64 & 0xff
		x >> 64 means shift 64 bits to left or x / 2^64 and x << 64 means shift 64 bits to right or x * 2^64
		& 0xff used to extract only the lowest 8 bits (1 byte since two chars) of a number
		byte extract with x >> 64 bits & 0xff ; bit extract with x >> i bits & 1
		byte2bit2hex process with bitMasking
		entropy2word and word2entropy using word <--> index using bitMasking and bitWising
		string -> bytes -> sha256Bytes -> 64Hex -> 256 bits zeros and one -> bigInt -> prvkey
		image -> base64 -> sha256 -> bytes > bigInt > hex -> prvkey -> address
		image  -> base64/58 string -> sha256Bytes -> 64Hex -> 256 bits zeros and one -> bigInt -> prvkey
		codec bitMask (7 - j ; hex to int ; lsb msdb) for msg like struct and files up/down ops in object storage format supports streaming with cacher over rmq with encoded data
		for byteIndex := range len(byteData){for bitIndex := range 8} for each byte iterate 8 times
		(| & ^ >> >>= ^= |= &= ~=) bitMasking and ways of converting byte to bits
		string or hex to bytes (string hex bytes) then to bits then codec on bits level with bitMasking and bigLittleEndian switch
		hex <-> bytes <-> string <-> bytes <-> bits <-> hex <-> int (fmt.Sprintf("%08b", int64(arr[i:i+2], 16)) or hex.DecodeString(arr[i:i+2]) or (hash160[i]>>j)&1)
		uint32(0) creates a 32 bits integer or 4 bytes number which
		sets all the bits to zero.
		0 in 32 bits is 0000...0000 which is 32 bits zeros
		or 8 hex chars 0xffffffff since 8 hex chars represents
		4 bytes which is 32 bits so ^uint32(0) is !0 in bits which
		would be 11111.....1111 32 bits ones
		adding this to an unsigned 32-bit counter (like atomic.Uint32)
		uses two's complement wraparound, effectively subtracting 1.
		result := uint(0) ^ uint(1) // oxr 32 bits zeros with 64 bits ones
	*/
	// decrement our counter
	workerCounter.Add(^uint32(0)) // ^uint32(0) subtract 1 from the atomicUint32

	// shift a pointer to the location of data stored on the ram for fast access and searching
	// 32 bits integer can be shown in hex as 8 hex chars 256 bits integer can be shown in hex as 64 hex chars
	dataOffsetInRam := 0xff36fa89                 // 32 bits integer offset since 8 hex chars is 4 bytes and 4 * 8 = 32
	dataOffsetInRamBytes := byte(dataOffsetInRam) // this makes the dataOffsetInRam integer
	byteSlicePointer := make([]byte, 4)           // since dataOffsetInRam is 8 hex chars we must allocate 4 bytes
	for bidx, _ := range byteSlicePointer {
		byteSlicePointer[bidx] >>= dataOffsetInRamBytes
	}

	// extract n bits from the number
	num := 20
	nBits := 10
	bins := make([]int, nBits)
	for i := 0; i < nBits; i++ {
		bins[i] = (num >> i) & 1         // fill in lsb
		bins[nBits-1-i] = (num >> i) & 1 // fill in msb; fill the last bit first; nBits - 1 is the last elem then nBits - 1 - i is dynamically filling the place of each bits
	}

	// following is incorrect Read requires a buffer with fixed size to fill it
	// and since slices are mutable pointer we should pass make([]byte, 32) to
	// Read to fill the 32 bytes with random values.
	// buffer := []byte{}
	// b, err := cryptoRandom.Read(&buffer)

	// bytes1 := make([]byte, 1)
	// bytes2 := []byte{}
	// bytesBuf := bytes.Buffer{}
	// bytes3 := bytesBuf.Bytes()

	stringness := "wildonion"
	string2byte := []byte(stringness)
	reconstruct := string(string2byte)
	print(reconstruct)

	// use bufio, bytes, bytes, io
	buf := bytes.NewBuffer([]byte{})
	bufwriter := bufio.NewWriter(buf)
	bufwriter.Write([]byte("wildonion"))

	hashOfString := sha256.New()
	hashOfString.Write(string2byte) // writes into the hasher
	hash := hashOfString.Sum(nil)
	hashHex := hex.EncodeToString(hash)
	fmt.Printf("%s", hashHex)

	_ = byte(0b1111111)             // sample bits into utf8 bytes
	randomBytes := make([]byte, 32) // this can be a hash
	cryptoRandom.Read(randomBytes)  // pass mutable pointer to fill the bytes in its scope; slices are mutable pointer by default
	hexString := hex.EncodeToString(randomBytes)
	integer, _ := new(big.Int).SetString(hexString, 16)
	bits := make([]byte, 256) // 256 bits or 32 byte 8 bits each
	print(integer)
	index := 0
	// the loop goes through all 256 bits cause 32 * 8 = 256
	// it's like we're falttening all the elements inside the 32 bytes array
	for i := 0; i < 32; i++ {
		for j := 0; j < 8; j++ {
			/*
				the value in ram is stored as bits already we just need to extract the bits in code with:
				(value >> n) & 1 which extracts the value (0 or 1) of the n-th bit from the number value
				this gives us the nth bit value of the number byte.
			*/
			// use (randomBytes[i] >> (7 - j)) & 1 for big endian order
			bits[index] = (randomBytes[i] >> j) & 1 // lsb little endian order
			index++                                 // it goes up to 256 bits since 32 * 8 = 256
		}
	}

	// convert big int to hex
	intergerBytes := integer.Bytes()
	integerHex := hex.EncodeToString(intergerBytes)
	print(integerHex)

	name := "wildonion"
	bytes := []byte(name)

	// showing "w" in 8 bits
	// 11101110
	fmt.Printf("ascii of w: %d\n", bytes[0])
	// shifting to right moves the bits to the right, so we're bringing the bit we're
	// interested in into the least significant bit (LSB) position.
	// & 1 is & 00000001
	print((bytes[0] >> 0) & 1)
	print((bytes[0] >> 1) & 1)
	print((bytes[0] >> 2) & 1)
	print((bytes[0] >> 3) & 1)
	print((bytes[0] >> 4) & 1)
	print((bytes[0] >> 5) & 1)
	print((bytes[0] >> 6) & 1)
	print((bytes[0] >> 7) & 1)

	hexStart, _ := hex.DecodeString("0x400000000")
	big := new(big.Int).SetBytes(hexStart)
	lowestByte := big.Int64() >> 64 & 0xff // divide by 2^64 then mask with 1 byte
	print(lowestByte)

	// uniqueKeys := make(map[string]bool)
	// prvkeys := make([]string, 200000000)
	// for i := range prvkeys {
	// 	_, prvkeyHex, err := generateRandomPrivateKey()
	// 	if err != nil {
	// 		continue
	// 	}
	// 	if !uniqueKeys[prvkeyHex] {
	// 		prvkeys[i] = prvkeyHex
	// 	}
	// }

	// uint32(0) create a 32 bits or 4 bytes number all bits set to zero
	bits32 := uint32(0)
	_ = 1 ^ bits32 | 0&1>>2 // we can do bitwise operations with that

	name1 := "wildonion"
	pname := &name1
	fmt.Printf("%p\n", pname)
	*pname = "newWildonion"
	fmt.Printf("%p\n", pname)
	newPName := "name"
	pname = &newPName
	fmt.Printf("%p\n", pname) // has now a different address since we've completely mutated with new pointer
}
